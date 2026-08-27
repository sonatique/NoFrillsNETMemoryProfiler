# No Frills .NET Memory Profiler — App Icons

## Deux variantes

- **Badge simplifie** (`svg/app-icon-simple.svg`) : fond vert accent plein,
  silhouette large sans detail. Utilise pour les tailles 16, 20, 24, 32, 40, 48
  (titlebar, taskbar, alt-tab) ou les details fins deviennent illisibles.
- **Icone detaillee** (`svg/app-icon-detailed.svg`) : la taupe complete, avec
  un petit chip violet ".NET / C#" en coin bas-droit qui identifie la
  technologie cible. Utilisee a partir de 64px (dock macOS, about box,
  splash, installeur). Le chip n'apparait pas sur le badge simplifie pour
  ne pas surcharger les petites tailles.

Le `.ico` Windows et l'arborescence Linux melangent deja les deux
automatiquement selon la taille.

## Contenu

```
windows/app.ico              Icone Windows multi-resolution (16 a 256)
macos/app.icns                Icone macOS (bundle .app)
linux/hicolor/...             Arborescence freedesktop hicolor
png-set/icon-*.png            Jeu complet de PNG (16 a 1024px)
svg/app-icon-simple.svg       Source vectorielle du badge (petites tailles)
svg/app-icon-detailed.svg     Source vectorielle detaillee + chip .NET (grandes tailles)
```

## Windows (.ico)

```xml
<PropertyGroup>
  <ApplicationIcon>Assets\app.ico</ApplicationIcon>
</PropertyGroup>
```

Copier `windows/app.ico` vers `Assets/app.ico` du projet.

## macOS (.icns)

```xml
<key>CFBundleIconFile</key>
<string>app.icns</string>
```

Placer `macos/app.icns` dans `Contents/Resources/` du bundle.

## Linux

1. **Icon theme standard** (recommande) : copier `linux/hicolor/` vers
   `/usr/share/icons/hicolor/` lors du packaging, referencer
   `no-frills-dotnet-memory-profiler` dans `Icon=` du fichier `.desktop`.
2. **Icone embarquee runtime** : `png-set/icon-256.png` avec `Window.Icon`.

## Avalonia — icone de fenetre (titlebar)

Utiliser le badge simplifie pour l'icone de fenetre (c'est elle qui
s'affiche en petit dans la barre de titre) :

```xml
<Window Icon="/Assets/icon-32.png" ...>
```

```csharp
Icon = new WindowIcon("Assets/icon-32.png");
```

## Tailles incluses dans png-set/

16, 20, 24, 32, 40, 48 -> badge simplifie
64, 72, 96, 128, 150, 180, 192, 256, 384, 512, 1024 -> icone detaillee + chip .NET
