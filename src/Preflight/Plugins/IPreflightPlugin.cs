﻿using Newtonsoft.Json;
using Preflight.Models;
using System.Collections.Generic;
#if NET472
using Umbraco.Core.Composing;
#else
using Umbraco.Cms.Core.Composing;
#endif

namespace Preflight.Plugins
{
    public interface IPreflightPlugin : IDiscoverable
    {
        /// <summary>
        /// This property must be set to indicate the plugin status
        /// Typically this would be done using a value returned in, or generated by, Check()
        /// </summary>
        [JsonProperty("failed")]
        bool Failed { get; set; }

        /// <summary>
        /// If a plugin contains multiple sub-tests, set this value to indicate the number of failed tests
        /// Will return 1 if not set
        /// </summary>
        [JsonProperty("failedCount")]
        int FailedCount { get; set; }

        /// <summary>
        /// If a plugin contains multiple sub-tests, set this value to indicate the total number of tests
        /// Will return 1 if not set
        /// </summary>
        [JsonProperty("totalTests")]
        int TotalTests { get; }

        /// <summary>
        /// Sets the sort for the plugin - determins order of display in the app
        /// </summary>
        [JsonIgnore]
        int SortOrder { get; }

        /// <summary>
        /// Name the plugin
        /// </summary>
        [JsonProperty("name")]
        string Name { get; }

        /// <summary>
        /// Provide a path to an AngularJs view to render the results in the backoffice
        /// Data will be passed as $scope.result
        /// </summary>
        [JsonProperty("viewPath")]
        string ViewPath { get; }

        /// <summary>
        /// Set to the result from the plugin's check, executed in Check()
        /// This can be any serializable object, which will be available as $scope.result in the backoffice
        /// </summary>
        [JsonProperty("result")]
        object Result { get; set; }

        /// <summary>
        /// The settings for this plugin
        /// </summary>
        [JsonProperty("settings")]
        IEnumerable<SettingsModel> Settings { get; set; }

        /// <summary>
        /// What does the plugin do, in brief, no markup
        /// </summary>
        [JsonProperty("summary")]
        string Summary { get; }

        /// <summary>
        /// What does the plugin do? Mark up welcome and will be trusted in the browser
        /// </summary>
        [JsonProperty("description")]
        string Description { get; set; }

        /// <summary>
        /// Must set both the Failed and Result values. How this is done is up to the implementation
        /// Params will be passed into this method when ContentChecker processes the plugin
        /// </summary>
        /// <param name="id">The current node id</param>
        /// <param name="culture">The variant culture to check</param>
        /// <param name="val">The string value of the current property</param>
        /// <param name="settings">The complete Preflight settings collection</param>
        void Check(int id, string culture, string val, List<SettingsModel> settings);
    }
}
