# CamTel

Utilise ton téléphone Android comme webcam sur Windows, en Wi-Fi ou en USB.
Une alternative libre et sans pub à DroidCam.

- **App Android** : filme avec la caméra arrière ou avant et diffuse le flux sur le réseau.
- **CamTel.exe** (Windows) : reçoit le flux et le publie comme une vraie webcam,
  **« CamTel Camera »**, visible dans Zoom, Teams, Discord, OBS, Google Meet (Chrome/Edge), Skype…

## Installation

1. Télécharge la [dernière release](https://github.com/Ghermin/camtel/releases/latest) :
   - `CamTel.apk` → installe-le sur le téléphone (autorise l'installation d'applis inconnues) ;
   - `CamTel.exe` → enregistre-le où tu veux sur le PC (un seul fichier, rien d'autre à installer).
2. Lance `CamTel.exe`. Au premier lancement, il propose d'installer la webcam virtuelle
   « CamTel Camera » (Windows demande l'autorisation administrateur une seule fois).
3. Ouvre l'app **CamTel** sur le téléphone.
4. Dans CamTel.exe, choisis le téléphone dans la liste puis **Connecter**.
5. Dans ton appli de visio, sélectionne la caméra **CamTel Camera**.

### Wi-Fi

Le PC et le téléphone doivent être sur le même réseau. Le téléphone est détecté automatiquement ;
sinon tape l'adresse affichée en haut de l'app (ex. `192.168.1.42`). Au premier lancement, autorise
CamTel dans le pare-feu Windows (réseaux privés) pour la détection automatique.

### USB (plus fluide, sans Wi-Fi)

1. Active le **débogage USB** sur le téléphone (Options pour les développeurs).
2. Installe `adb` sur le PC ([Android platform-tools](https://developer.android.com/tools/releases/platform-tools))
   et mets-le dans le `PATH` (CamTel le trouve aussi dans `%LOCALAPPDATA%\Android\Sdk\platform-tools`).
3. Branche le téléphone : il apparaît comme `USB · <modèle>` dans la liste.

## Fonctionnalités

| Téléphone | PC |
|---|---|
| Caméra arrière / avant | Sortie 640×480, 1280×720 ou 1920×1080 |
| Résolution 480p / 720p / 1080p | Rotation 0/90/180/270°, miroir |
| Flash, zoom, qualité JPEG | Adapter ou remplir (rogner) |
| Écran noir pour économiser la batterie | Reconnexion automatique, icône près de l'horloge |
| Visionneuse web : `http://<ip-du-tel>:4747/` | Image d'attente quand le téléphone est déconnecté |

Garde l'app ouverte au premier plan sur le téléphone (utilise **Écran noir** pour économiser
la batterie) : Android coupe la caméra des applis en arrière-plan.

## Désinstallation

Menu **Webcam virtuelle → Désinstaller** dans CamTel.exe, puis supprime `CamTel.exe`.
Les réglages sont dans `%APPDATA%\CamTel`, le pilote dans `%ProgramData%\CamTel`.

## Fonctionnement

```
Téléphone (CameraX → JPEG)  ──HTTP MJPEG :4747──▶  CamTel.exe  ──mémoire partagée──▶  filtre DirectShow « CamTel Camera »  ──▶  Zoom, Teams…
            └── balise UDP :4748 (détection auto)
```

API HTTP du téléphone :

| Chemin | Rôle |
|---|---|
| `GET /video` | flux MJPEG (`multipart/x-mixed-replace`) |
| `GET /status` | état en JSON |
| `GET /control?camera=front\|back\|toggle&torch=on\|off&res=1280x720&quality=80&zoom=0.5` | réglages |

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
