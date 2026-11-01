# Pilote de webcam virtuelle

Copie de [tshino/softcam](https://github.com/tshino/softcam) v1.8.1 (licence MIT, voir `LICENSE`),
un filtre DirectShow de type « Video Input Device ».

Modifications pour CamTel :

- nom du périphérique : `CamTel Camera` (au lieu de `DirectShow Softcam`) ;
- CLSID : `{61A0B261-FCBA-4EF9-824C-A9C6D2639911}` ;
- noms de la mémoire partagée / du mutex préfixés `CamTel Camera/` pour ne pas entrer en conflit
  avec d'autres logiciels basés sur softcam.

Compilation (Visual Studio 2022, C++) :

```
msbuild softcam.sln /t:softcam /p:Configuration=Release /p:Platform=x64
msbuild softcam.sln /t:softcam /p:Configuration=Release /p:Platform=Win32
```

Les DLL sont produites dans `dist/bin/<plateforme>/softcam.dll`. La CI GitHub les compile et
les embarque dans `CamTel.exe`, qui les installe lui-même (`regsvr32`) au premier lancement.
