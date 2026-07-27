# Chat en Direct — Plugin Jellyfin

Messagerie temps réel (par polling) entre les utilisateurs d'un serveur Jellyfin.

## Fonctionnalités

- **Salon public** partagé par tous les utilisateurs.
- **Messages privés** entre deux membres (onglets, comme sur les captures).
- **Liste des membres** avec nom + avatar Jellyfin, pour démarrer une conversation.
- **Amis** : demande / acceptation / retrait.
- **Blocage** : bloquer / débloquer un utilisateur (coupe les DM dans les deux sens).
- **Panel de modération admin** : vider un salon, supprimer un message, **bannir** (lecture+écriture) ou **rendre muet** (écriture) un utilisateur, temporaire ou permanent.
- **GIF / images** par URL, petit sélecteur d'emojis.

## Architecture

| Couche | Détail |
|---|---|
| Backend | Plugin .NET 8, API REST sous `/ChatPlugin/*`, stockage **SQLite** (`chat.db` dans le dossier de config du plugin). |
| Temps réel | **Polling** toutes les 3 s côté client. |
| Frontend | `client.js` + `client.css` embarqués, injectés dans `index.html` du client web au démarrage. |
| Auth | Réutilise l'authentification Jellyfin (`IAuthorizationContext`) ; les routes admin exigent `RequiresElevation`. |

### Fichiers

```
Plugin.cs                     Point d'entrée du plugin + page de config
PluginServiceRegistrator.cs   Enregistrement DI (DB, resolver, injection web)
Configuration/                Config persistante + page admin HTML
Data/ChatDatabase.cs          Toute la couche SQLite
Models/                       Entités + DTOs
Services/UserResolver.cs      Utilisateurs Jellyfin -> DTO (nom, avatar, admin)
Services/WebInjectionService.cs  Injection du <script> dans index.html
Controllers/                  API : Chat, Friends, Admin, Assets (js/css)
Web/client.js, client.css     Interface du chat
```

## Compilation

Prérequis : **.NET 8 SDK**.

```bash
cd Jellyfin.Plugin.Chat
dotnet build -c Release
```

Le plugin produit `bin/Release/net8.0/Jellyfin.Plugin.Chat.dll`.

> Les versions des packages `Jellyfin.Controller` / `Jellyfin.Model` (10.10.3) doivent correspondre à ta version de serveur. Ajuste-les dans le `.csproj` si besoin.

## Installation manuelle

1. Crée un dossier dans le répertoire de plugins de Jellyfin :
   - Linux (natif) : `/var/lib/jellyfin/plugins/Chat_1.0.0.0/`
   - Docker : `/config/plugins/Chat_1.0.0.0/`
   - Windows : `%ProgramData%\Jellyfin\Server\plugins\Chat_1.0.0.0\`
2. Copie `Jellyfin.Plugin.Chat.dll` dedans.
3. **Redémarre** le serveur Jellyfin.
4. Le script est injecté automatiquement dans le client web ; un bouton 💬 apparaît en bas à droite.

> Si l'UI n'apparaît pas : vide le cache du navigateur, et vérifie dans les logs la ligne `[Chat] Script du chat injecte`.

## Configuration

Tableau de bord → Plugins → **Chat en Direct**. On peut activer/désactiver le salon public, les DM, les médias, régler les limites, ou désactiver l'injection auto.

## Points d'attention

- **Injection `index.html`** : chaque mise à jour de Jellyfin réécrit le client web ; le plugin ré-injecte au démarrage, mais il faut redémarrer après une MAJ.
- **Polling** : simple et robuste ; pour du vrai temps réel on pourrait passer à SignalR dans une v2.
- **Avatars** : servis par Jellyfin (`/Users/{id}/Images/Primary`) ; les utilisateurs sans photo affichent leurs initiales.

## Pistes v2

- Notifications non lues par onglet + badge sur le lanceur.
- Indicateur « en train d'écrire » et présence réelle (via `/Sessions`).
- Intégration Giphy (recherche de GIF) au lieu du collage d'URL.
- WebSocket/SignalR pour le temps réel.
