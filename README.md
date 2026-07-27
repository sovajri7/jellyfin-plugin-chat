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

Prérequis : **.NET 9 SDK** (Jellyfin 10.11 tourne sur .NET 9).

```bash
cd Jellyfin.Plugin.Chat
dotnet build -c Release
```

Le plugin produit `bin/Release/net9.0/Jellyfin.Plugin.Chat.dll`.

> Les versions des packages `Jellyfin.Controller` / `Jellyfin.Model` (10.10.3) doivent correspondre à ta version de serveur. Ajuste-les dans le `.csproj` si besoin.

## ⚠️ Prérequis : plugin « File Transformation »

L'interface de chat est un script injecté dans le client web Jellyfin. Depuis la 1.1.1.0, l'injection se fait **au moment où la page est servie**, via le plugin communautaire **[File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)** — ce qui évite d'écrire sur le disque et **survit aux mises à jour de Jellyfin**.

**Installe-le d'abord** :
1. Tableau de bord → Plugins → Dépôts → **+** :
   ```
   https://www.iamparadox.dev/jellyfin/plugins/manifest.json
   ```
2. Catalogue → installe **File Transformation** → redémarre.

> Sans File Transformation, le plugin retombe sur l'écriture directe de `web/index.html`, qui n'est possible que si ce fichier est accessible en écriture par le process Jellyfin (`sudo chown jellyfin /usr/share/jellyfin/web/index.html`), à refaire après chaque mise à jour du client web.

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

### Recherche de GIF (Klipy)

La recherche de GIF intégrée passe par [Klipy](https://klipy.com/developers). **Chaque administrateur qui installe le plugin doit créer sa propre clé API** (gratuit) et la coller dans la config du plugin. La clé reste côté serveur (proxy `/ChatPlugin/gif/search`) et n'est jamais exposée aux navigateurs. Sans clé, le bouton GIF retombe sur le collage d'une URL d'image.

## Compatibilité

Ciblé pour **Jellyfin 10.11.x** (compilé contre `Jellyfin.Controller` 10.11.11, `targetAbi` 10.11.0.0). Pour une autre version majeure, ajuste les versions de packages dans le `.csproj` et l'ABI dans `build.yaml` / `.github/update_manifest.py`.

## Points d'attention

- **Injection `index.html`** : chaque mise à jour de Jellyfin réécrit le client web ; le plugin ré-injecte au démarrage, mais il faut redémarrer après une MAJ.
- **Polling** : simple et robuste ; pour du vrai temps réel on pourrait passer à SignalR dans une v2.
- **Avatars** : servis par Jellyfin (`/Users/{id}/Images/Primary`) ; les utilisateurs sans photo affichent leurs initiales.

## Pistes v2

- Notifications non lues par onglet + badge sur le lanceur.
- Indicateur « en train d'écrire » et présence réelle (via `/Sessions`).
- Intégration Giphy (recherche de GIF) au lieu du collage d'URL.
- WebSocket/SignalR pour le temps réel.
