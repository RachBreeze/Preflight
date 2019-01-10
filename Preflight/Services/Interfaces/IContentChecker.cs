﻿using Preflight.Models;
using Umbraco.Core.Models;

namespace Preflight.Services.Interfaces
{
    public interface IContentChecker
    {
        PreflightResponseModel Check(IContent content);
        IContent Autoreplace(IContent content);
    }
}
