<p align="center"><img src="docs/icon-192.png" width="96" alt="CamTel"></p>

<h1 align="center">CamTel</h1>

Utilise ton téléphone Android comme webcam sur Windows, en Wi-Fi ou en USB.
Une alternative libre et sans pub à DroidCam.

**➜ Téléchargement : https://ghermin.github.io/camtel/** (toujours la dernière version)

- **App Android** : filme avec la caméra arrière ou avant et diffuse le flux sur le réseau.
- **CamTel.exe** (Windows) : reçoit le flux et le publie comme une vraie webcam,
  **« CamTel Camera »**, visible dans Zoom, Teams, Discord, OBS, Google Meet (Chrome/Edge), Skype…

## Installation

**PC (Windows 10/11 64 bits)** : télécharge
[`CamTel.exe`](https://github.com/Ghermin/camtel/releases/latest/download/CamTel.exe) et lance-le.

- Si SmartScreen affiche « Windows a protégé votre ordinateur » (l'exe n'est pas signé) :
  **Informations complémentaires → Exécuter quand même**.
- CamTel s'installe tout seul : menu Démarrer, démarrage avec Windows (icône près de l'horloge).
  Il installe ensuite la webcam « CamTel Camera » (une autorisation administrateur Windows).

**Téléphone (Android 8+)**, au choix :

- **Wi-Fi** : scanne le QR code affiché par CamTel sur le PC avec l'appareil photo, installe
  l'APK, ouvre l'app. Le PC trouve le téléphone sur le réseau et se connecte tout seul, sans
  réglage de pare-feu.
- **USB** : branche le téléphone avec le
  [débogage USB](https://developer.android.com/studio/debug/dev-options) activé, puis accepte
  « Autoriser le débogage USB ? ». CamTel installe ou met à jour l'app sur le téléphone, donne
  l'accès caméra, la lance et se connecte. Les outils Android (`adb`) sont téléchargés depuis
  Google au premier lancement s'ils manquent. L'app du téléphone explique comment activer le
  débogage USB (touche le cadre d'infos en haut à gauche).

Ensuite, choisis la caméra **CamTel Camera** dans ton appli de visio. Si l'appli était déjà
ouverte avant l'installation, quitte-la complètement et relance-la.

**Mises à jour automatiques**, au prochain lancement :

- **CamTel.exe** vérifie GitHub à chaque démarrage, avant de se connecter au téléphone, et
  s'installe aussitôt s'il y a une nouvelle version. Pendant la journée, il vérifie aussi toutes
  les 6 h, mais n'applique la mise à jour qu'une fois la diffusion terminée (jamais pendant une
  visio).
- **L'app du téléphone** vérifie à chaque ouverture et s'installe elle-même. Elle se ferme alors,
  et une notification « CamTel mis à jour » permet de la rouvrir.
- **Branché en USB**, c'est le PC qui met à jour l'app du téléphone et la relance.
- Si l'app a été installée ou mise à jour une fois par le PC en USB, ses mises à jour suivantes
  sont silencieuses. Sinon, Android demande une confirmation la première fois.

CamTel préfère le câble quand il est branché, repasse en Wi-Fi si tu le débranches, et relance
l'app du téléphone si elle a été fermée pendant que le câble est branché. Pour une adresse IP
précise, tape-la dans le sélecteur de téléphone puis appuie sur Entrée.

## Fonctionnalités

Les mêmes boutons existent sur le téléphone et sur le PC, et restent **synchronisés dans les
deux sens** : un changement fait d'un côté s'affiche aussitôt de l'autre.

| Réglage (tel ⇄ PC) | Rôle |
|---|---|
| Caméra avant / arrière | |
| Flash | caméra arrière |
| Miroir | appliqué à l'aperçu du tel et à la webcam |
| 480p / 720p / 1080p | résolution envoyée au PC |
| 30 ips / Nuit | 30 ips fixes (fluide) ou mode nuit (plus lumineux, ips variables) |
| Écran noir | éteint l'écran du tel, la caméra continue |
| Zoom, luminosité (EV), qualité JPEG | curseurs |

Côté PC uniquement : format de sortie de la webcam (640×480, 1280×720, 1920×1080), rotation,
remplir (rogner au lieu des bandes noires), reconnexion automatique, icône près de l'horloge et
image d'attente quand le téléphone est déconnecté.

Le téléphone affiche exactement les images envoyées au PC, en version allégée. La diffusion
continue quand l'app est en arrière-plan ou que l'écran est verrouillé (notification « CamTel
diffuse la caméra », bouton **Arrêter**). Visionneuse web : `http://<ip-du-tel>:4747/`.

**Batterie et ressources**

- **Téléphone** : la caméra ne tourne que si un PC reçoit l'image ou si l'app est affichée. Sinon,
  elle s'éteint (notification « CamTel en veille ») et se rallume dès qu'un PC se connecte.
- Sans PC depuis 2 minutes et app en arrière-plan, tout s'arrête.
- **⏻**, ou glisser l'app hors des applis récentes, ferme tout, processus compris.
- L'écran ne reste allumé que pendant qu'un PC est connecté, et l'aperçu seul tourne à 15 ips.
- **PC** : quand la fenêtre est cachée, CamTel ne calcule plus d'aperçu, sonde le réseau moins
  souvent et détecte le branchement USB par événement Windows.
- **Quitter** (icône près de l'horloge) arrête aussi le serveur adb s'il a été démarré par CamTel.

## Désinstallation

Menu **⋮ → Désinstaller CamTel de ce PC** : supprime la webcam virtuelle, le raccourci, le démarrage automatique et les réglages.
Les réglages sont dans `%APPDATA%\CamTel`, le pilote dans `%ProgramData%\CamTel`.

## Fonctionnement

```
Téléphone (CameraX → JPEG)  ──HTTP MJPEG :4747──▶  CamTel.exe  ──mémoire partagée──▶  filtre DirectShow « CamTel Camera »  ──▶  Zoom, Teams…
        ▲ le PC sonde le réseau local sur le port 4747 (ou adb forward en USB)
```

API HTTP du téléphone :

| Chemin | Rôle |
|---|---|
| `GET /video` | flux MJPEG (`multipart/x-mixed-replace`) |
| `GET /status` | état en JSON ; `?since=<version>` attend un changement (long-polling, 20 s max) |
| `GET /control?camera=front\|back\|toggle&torch=on\|off&mirror=on\|off&night=on\|off&blackout=on\|off&res=1280x720&quality=80&zoom=0.5&exposure=-1.0` | réglages |

### Limites connues

La webcam virtuelle est un filtre DirectShow : elle fonctionne dans la grande majorité des applis
(navigateurs, Zoom, Teams, Discord, OBS…), mais pas dans l'appli **Caméra** de Windows, qui
n'utilise que Media Foundation.

## Compiler

| Partie | Dossier | Outils |
|---|---|---|
| App Android | `android/` | JDK 17, SDK Android 35 : `./gradlew assembleDebug` |
| App Windows | `windows/CamTel/` | SDK .NET 8 : `dotnet publish -c Release` |
| Pilote | `driver/` | Visual Studio 2022 (C++) — voir [driver/README.md](driver/README.md) |

Sans Visual Studio, `scripts/fetch-driver.ps1` récupère les DLL du pilote depuis la dernière release.
La CI (`.github/workflows/build.yml`) compile les trois parties à chaque push et publie une release
pour chaque tag `v*`.

## Licence

MIT. Le pilote est basé sur [softcam](https://github.com/tshino/softcam) de tshino (MIT).
