# Bored Dev Playground

Ce projet fait partie du Bored Dev Playground : https://ghermin.github.io/ (repo `Ghermin/Ghermin.github.io`, en local `../bored-dev-playground`).
Sa page GitHub (`docs/index.html`) suit le kit commun du playground.

Avant de modifier la page ou le style, se demander si le changement est local ou global :

- **Global** (bandeau, cartes, boutons, puces, pied de page, couleurs neutres, comportement clair / sombre…) : ne pas le faire ici. `docs/playground.css` est une copie de `kit/playground.css` du playground : modifier le kit là-bas, lancer `sh kit/sync.sh`, puis commiter dans chaque projet.
- **Local** (couleurs du projet) : le faire dans la page, en redéfinissant les variables `--pg-*` après le kit.
- Si le nom, la description, les tags, l'icône ou l'adresse du projet changent, mettre à jour sa carte dans `index.html` du playground.
- Icônes (favicons, icônes Android et Windows, logo de la page) : générées par le playground à partir de `brand/icons/camtel.svg` (`node build.js camtel` dans `brand/`). Ne pas les redessiner ici : modifier le SVG là-bas, relancer, puis commiter dans les deux repos.

# Sécurité du serveur du téléphone

Le serveur HTTP de l'app (`StreamServer.kt`, port 4747) n'accepte que les PC appairés (voir « Authentification » dans le README) :

- Toute nouvelle route passe par `controller.authorized(...)` ; seule `/pair` en est dispensée. Sans preuve : 401 avec seulement l'identité publique (`identityJson`), jamais l'état ni l'image.
- Garder `allowed()` (Host = adresse IP, pas d'`Origin` / `Sec-Fetch-Site` étranger) et ne jamais envoyer d'en-tête CORS.
- Le secret (`Pairing.kt`) ne sort que par `/pair` (code à 6 chiffres, app à l'écran, 1 essai/s, nouveau code après 5 échecs) ou par `PairingProvider` (uid shell/root seulement, lu par adb). Il ne passe jamais dans une URL ni dans les journaux.
- Côté PC, la découverte (`PhoneDiscovery`) interroge tout le sous-réseau : elle ne doit jamais envoyer de secret ni de preuve. Les preuves (`PhoneAuth.Header`) sont calculées pour l'adresse réellement jointe.
- Un changement de protocole touche les deux applis : changer la version des deux (`build.gradle.kts`, `CamTel.csproj`) pour que le PC mette à jour le téléphone en USB.

À garder sur la page : le bandeau `pg-strip` qui ramène au playground, le thème clair / sombre automatique, le responsive (pas de défilement horizontal sur mobile), les 2 polices du playground et aucune autre (Bricolage Grotesque via `--pg-font` du kit, JetBrains Mono via `--pg-mono`), les couleurs du dégradé de son icône (`--pg-accent` / `--pg-accent-2`) sans autre couleur décorative.
