using System;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Chat.Controllers;

/// <summary>
/// Base commune : resolution de l'utilisateur authentifie via IAuthorizationContext
/// (disponible dans le package Jellyfin.Controller, contrairement aux extensions de Jellyfin.Api).
/// </summary>
public abstract class ChatControllerBase : ControllerBase
{
    private readonly IAuthorizationContext _authContext;

    protected ChatControllerBase(IAuthorizationContext authContext)
    {
        _authContext = authContext;
    }

    /// <summary>Utilisateur courant (jamais null pour une route [Authorize]).</summary>
    protected async Task<User?> GetCurrentUserAsync()
    {
        var info = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        return info.User;
    }

    protected async Task<Guid> GetCurrentUserIdAsync()
    {
        var info = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        return info.UserId;
    }
}
