using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Chat.Models;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Chat.Services;

/// <summary>
/// Traduit les utilisateurs Jellyfin en DTOs pour le chat (nom, avatar, admin).
/// </summary>
public sealed class UserResolver
{
    private readonly IUserManager _userManager;

    public UserResolver(IUserManager userManager)
    {
        _userManager = userManager;
    }

    public User? GetUser(Guid id) => _userManager.GetUserById(id);

    public string GetName(Guid id) => _userManager.GetUserById(id)?.Username ?? "Inconnu";

    public bool IsAdmin(Guid id)
    {
        var u = _userManager.GetUserById(id);
        return u is not null && u.HasPermission(PermissionKind.IsAdministrator);
    }

    /// <summary>
    /// URL relative de l'avatar (a prefixer cote client par l'adresse du serveur),
    /// ou null si l'utilisateur n'a pas d'image de profil.
    /// </summary>
    public string? GetAvatarUrl(User user)
    {
        if (user.ProfileImage is null)
        {
            return null;
        }

        // Cache-buster base sur la date de derniere modif de l'image.
        var tag = user.ProfileImage.LastModified.Ticks;
        return $"Users/{user.Id:N}/Images/Primary?tag={tag}";
    }

    public IEnumerable<User> AllUsers() => _userManager.Users;

    public ChatUserDto ToDto(User user) => new()
    {
        Id = user.Id.ToString("N"),
        Name = user.Username,
        AvatarUrl = GetAvatarUrl(user),
        IsAdmin = user.HasPermission(PermissionKind.IsAdministrator)
    };
}
