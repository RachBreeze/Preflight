﻿using Newtonsoft.Json.Linq;
using Preflight.Constants;
using Preflight.Extensions;
using Preflight.Hubs;
using Preflight.Models;
using Preflight.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
#if NET472
using Microsoft.AspNet.SignalR;
using Preflight.Logging;
using Umbraco.Core;
using Umbraco.Core.Configuration.Grid;
using Umbraco.Core.Models;
using Umbraco.Core.Services;
using IProperty = Umbraco.Core.Models.Property;
#else
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Configuration.Grid;
using Umbraco.Cms.Core.Services;
#endif

namespace Preflight.Services.Implement
{
    /// <summary>
    /// Where the magic happens. ContentChecker extracts property values and passes them into the set of plugins for testing
    /// </summary>
    internal class ContentChecker : IContentChecker
    {
        private readonly IContentService _contentService;
        private readonly IContentTypeService _contentTypeService;
        private readonly ISettingsService _settingsService;
        private readonly ILogger<ContentChecker> _logger;
        private readonly PreflightPluginCollection _plugins;

#if NET472
        private readonly IHubContext<IPreflightHub> _hubContext;
#else
        private readonly IHubContext<PreflightHub, IPreflightHub> _hubContext;
#endif

        private int _id;
        private bool _fromSave;
        private List<SettingsModel> _settings;
        private string _testableProperties;

        private readonly IEnumerable<IGridEditorConfig> _gridEditorConfig;

#if NET472
        public ContentChecker(ISettingsService settingsService, IContentService contentService, IGridConfig gridConfig,
            IContentTypeService contentTypeService, ILogger<ContentChecker> logger, PreflightPluginCollection plugins)
#else
        public ContentChecker(IHubContext<PreflightHub, IPreflightHub> hubContext, ISettingsService settingsService, IOptions<IGridConfig> gridConfig,
            IContentService contentService, IContentTypeService contentTypeService, ILogger<ContentChecker> logger, PreflightPluginCollection plugins)
#endif
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _contentService = contentService ?? throw new ArgumentNullException(nameof(contentService));
            _contentTypeService = contentTypeService ?? throw new ArgumentNullException(nameof(contentTypeService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));

#if NET472
            _hubContext = GlobalHost.ConnectionManager.GetHubContext<PreflightHub, IPreflightHub>();
            _gridEditorConfig = gridConfig.EditorsConfig.Editors;
#else
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _gridEditorConfig = gridConfig.Value.EditorsConfig.Editors;
#endif
        }

        /// <summary>
        /// Intialise variables for this testing run
        /// </summary>
        private void Initialise(string culture)
        {
            _settings = _settingsService.Get().Settings;
            _testableProperties = _settings.GetValue<string>(KnownSettings.PropertiesToTest, culture);
        }

        private void SendTestResult(PreflightPropertyResponseModel model)
        {
            _hubContext.Clients.All.preflightTest(model);
        }

        private void PreflightComplete()
        {
            _hubContext.Clients.All.preflightComplete();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="dirtyProperties"></param>
        public bool CheckDirty(DirtyProperties dirtyProperties)
        {
            Initialise(dirtyProperties.Culture);

            _id = dirtyProperties.Id;
            var failed = false;

            foreach (SimpleProperty prop in dirtyProperties.Properties)
            {
                string propValue = prop.Value?.ToString();

                // only continue if the prop has a value
                if (!propValue.HasValue())
                {
                    SendTestResult(new PreflightPropertyResponseModel
                    {
                        Name = prop.Name,
                        Remove = true
                    });

                    continue;
                }

                failed = TestAndBroadcast(prop.Name, dirtyProperties.Culture, propValue, prop.Editor) || failed;
            }

            PreflightComplete();

            return failed;
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="id"></param>
        /// <param name="fromSave"></param>
        /// <returns></returns>
        public bool CheckContent(int id, string culture, bool fromSave) => CheckContent(_contentService.GetById(id), culture, fromSave);


        /// <summary>
        /// Checks all testable properties on the given IContent item
        /// </summary>
        /// <param name="content"></param>
        /// <param name="fromSave"></param>
        /// <returns></returns>
        public bool CheckContent(IContent content, string culture, bool fromSave)
        {
            Initialise(culture);

            _fromSave = fromSave;
            var failed = false;

            IEnumerable<IProperty> props = content.GetPreflightProperties();

            foreach (IProperty prop in props)
            {
                string propValue = (prop.GetValue(culture) ?? prop.GetValue()).ToString();

                // only continue if the prop has a value
                if (!propValue.HasValue())
                {
                    SendTestResult(new PreflightPropertyResponseModel
                    {
                        Name = prop.PropertyType.Name,
                        Remove = true
                    });

                    continue;
                }

                failed = TestAndBroadcast(prop.PropertyType.Name, culture, propValue, prop.PropertyType.PropertyEditorAlias) || failed;
            }

            PreflightComplete();

            return failed;
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        /// <param name="alias"></param>
        /// <returns></returns>
        private bool TestAndBroadcast(string name, string culture, string value, string alias)
        {
            List<PreflightPropertyResponseModel> testResult = new List<PreflightPropertyResponseModel>();

            bool failed = false;

            switch (alias)
            {
                case KnownPropertyAlias.NestedContent:
                    testResult = ExtractValuesFromNestedContentProperty(name, culture, value);
                    break;
                case KnownPropertyAlias.Grid:
                    testResult = ExtractValuesFromGridProperty(name, culture, value);
                    break;
                case KnownPropertyAlias.Rte:
                case KnownPropertyAlias.Textarea:
                case KnownPropertyAlias.Textbox:
                    testResult = new[] { RunPluginsAgainstValue(name, culture, value, alias) }.ToList();
                    break;
            }

            // return the results via signalr for perceived perf
            foreach (PreflightPropertyResponseModel result in testResult)
            {
                // ignore results where no plugins ran
                if (result.Plugins.Count > 0)
                {
                    if (result.Failed)
                    {
                        failed = true;
                    }

                    // announce the result
                    SendTestResult(result);
                }
            }

            return failed;
        }


        /// <summary>
        /// Extracts the testable values from a Nested Content instance, and passes each to <see cref="ProcessJTokenValues" />
        /// </summary>
        /// <param name="propName"></param>
        /// <param name="propValue"></param>
        /// <returns></returns>
        private List<PreflightPropertyResponseModel> ExtractValuesFromNestedContentProperty(string propName, string culture, string propValue)
        {
            Dictionary<string, IContentType> cache = new Dictionary<string, IContentType>();
            List<PreflightPropertyResponseModel> response = new List<PreflightPropertyResponseModel>();

            JArray asJson = JArray.Parse(propValue);
            var index = 1;

            foreach (JObject o in asJson)
            {
                var typeAlias = o.Value<string>(KnownStrings.NcAlias);
                var type = cache.ContainsKey(typeAlias) ? cache[typeAlias] : _contentTypeService.Get(typeAlias);

                if (!cache.ContainsKey(typeAlias))
                {
                    cache.Add(typeAlias, type);
                }

                var propsFromType = type.GetPreflightProperties();

                foreach (var property in propsFromType)
                {
                    var value = o.Value<string>(property.Alias);
                    string label = $"{propName} (Item {index} - {property.Name})";

                    if (!value.HasValue())
                    {
                        SendTestResult(new PreflightPropertyResponseModel
                        {
                            Name = property.Name,
                            Label = label,
                            Remove = true
                        });
                    }
                    else
                    {
                        PreflightPropertyResponseModel model = RunPluginsAgainstValue(propName, culture, value, property.PropertyEditorAlias, KnownPropertyAlias.NestedContent);

                        model.Label = label;
                        model.TotalTests = model.Plugins.Aggregate(0, (acc, x) => acc + x.TotalTests);

                        response.Add(model);
                    }
                }

                index += 1;
            }

            return response;
        }


        /// <summary>
        /// Extracts the testable values from a single Grid editor, and passes each to <see cref="ProcessJTokenValues" />
        /// </summary>
        /// <param name="propName"></param>
        /// <param name="propValue"></param>
        /// <returns></returns>
        private List<PreflightPropertyResponseModel> ExtractValuesFromGridProperty(string name, string culture, string propValue)
        {
            JObject asJson = JObject.Parse(propValue);
            List<PreflightPropertyResponseModel> response = new List<PreflightPropertyResponseModel>();

            IEnumerable<JToken> rows = asJson.SelectTokens("..rows");
            foreach (JToken row in rows)
            {
                string rowName = row[0].Value<string>("name");
                IEnumerable<JToken> editors = row.SelectTokens(KnownStrings.GridRteJsonPath);

                foreach (JToken editor in editors)
                {
                    // this is a bit messy - maps the grid editor view to the knownpropertyalias value
                    var editorViewAlias = "";
                    var editorAlias = editor.SelectToken("$..editor.alias")?.ToString();

                    var gridEditorConfig = _gridEditorConfig.FirstOrDefault(x => x.Alias == editorAlias);
                    var gridEditorName = gridEditorConfig.Name;
                    var gridEditorView = gridEditorConfig.View;

                    string label = $"{name} ({rowName} - {gridEditorName})";

                    JToken value = editor.SelectToken(KnownStrings.GridValueJsonPath);
                    if (value == null || !value.ToString().HasValue())
                    {
                        SendTestResult(new PreflightPropertyResponseModel
                        {
                            Name = gridEditorName,
                            Label = label,
                            Remove = true
                        });
                    }
                    else
                    {
                        if (gridEditorView.HasValue())
                        {
                            switch (gridEditorView)
                            {
                                case "rte": editorViewAlias = KnownPropertyAlias.Rte; break;
                                case "textstring": editorViewAlias = KnownPropertyAlias.Textbox; break;
                                case "textarea": editorViewAlias = KnownPropertyAlias.Textarea; break;
                            }
                        }

                        PreflightPropertyResponseModel model = RunPluginsAgainstValue(name, culture, value.ToString(), editorViewAlias, KnownPropertyAlias.Grid);

                        model.Label = label;
                        model.TotalTests = model.Plugins.Aggregate(0, (acc, x) => acc + x.TotalTests);

                        response.Add(model);
                    }
                }
            }

            return response;
        }


        /// <summary>
        /// Runs the set of plugins against the given string
        /// </summary>
        /// <param name="name"></param>
        /// <param name="val"></param>
        /// <returns></returns>
        private PreflightPropertyResponseModel RunPluginsAgainstValue(string name, string culture, string val, string alias, string parentAlias = "")
        {
            var model = new PreflightPropertyResponseModel
            {
                Label = name,
                Name = name
            };

            if (val == null || !_testableProperties.Contains(alias) || (parentAlias.HasValue() && !_testableProperties.Contains(parentAlias)))
                return model;

            foreach (IPreflightPlugin plugin in _plugins)
            {
                // settings on the plugin are the defaults - set to correct values from _settings
                plugin.Settings = _settings.Where(s => s.Tab == plugin.Name)?.ToList();

                // ignore disabled plugins
                if (plugin.IsDisabled()) continue;
                if (!_fromSave && plugin.IsOnSaveOnly()) continue;

                string propsValue = plugin.Settings.FirstOrDefault(x => x.Alias.Contains("PropertiesToTest"))?.Value.ForVariant(culture);
                string propsToTest = propsValue ?? string.Join(",", KnownPropertyAlias.All);

                // only continue if the field alias is include for testing, or the parent alias has been set, and is included for testing
                if (!propsToTest.Contains(alias) || (parentAlias.HasValue() && !propsToTest.Contains(parentAlias))) continue;

                try
                {
                    Type pluginType = plugin.GetType();
                    if (pluginType.GetMethod("Check") == null) continue;

                    plugin.Check(_id, culture, val, _settings);

                    if (plugin.Result != null)
                    {
                        if (plugin.FailedCount == 0)
                        {
                            plugin.FailedCount = plugin.Failed ? 1 : 0;
                        }

                        model.Plugins.Add(plugin);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Preflight couldn't take off: {Message}", e.Message);
                }
            }

            // mark as failed if any sub-tests have failed
            model.FailedCount = model.Plugins.Sum(x => x.FailedCount);
            model.Failed = model.FailedCount > 0;

            model.Plugins = model.Plugins.OrderBy(p => p.SortOrder).ToList();
            model.TotalTests = model.Plugins.Aggregate(0, (acc, x) => acc + x.TotalTests);

            return model;
        }
    }
}
