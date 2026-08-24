# KSP-BulldozerMod — nettoyer une zone de ses rochers (scatters)

> **Statut : refonte complète de l'objectif (2026-08-24).**
> Le mod ne cherche **plus** à niveler le terrain. Nouvel objectif : **supprimer les éléments décoratifs posés au sol (rochers Parallax, scatters stock) dans une zone**, **sans toucher au relief**.
>
> Ce README est le point d'entrée pour reprendre le sujet à froid (y compris dans une nouvelle conversation Claude). L'historique détaillé de l'ancienne approche (nivellement PQS) reste dans [CLAUDE.md](CLAUDE.md) — il est **périmé quant à l'objectif**, mais reste précieux pour les connaissances PQS qu'il contient.

## 1. Le besoin réel

Je construis mes bases planétaires avec cette technique (validée en jeu, elle marche) :

1. un **Clamp-O-Tron** posé au sol par un ingénieur ;
2. en **construction EVA**, des **poutrelles** assemblées pour former une structure qui **épouse la forme du sol** ;
3. des **ports de docking verticaux** sur cette structure ;
4. une **grue** qui vient docker les modules à la verticale dessus.

Pourquoi cette technique plutôt que « poser des vaisseaux et les docker entre eux » : cette dernière ne survit pas au rechargement. KSP **ne mémorise pas les forces appliquées entre les parties** ; il repositionne la base sans contraintes, une pièce se retrouve dans le sol, la physique démarre, la base est propulsée vers le haut et tout explose. Avec une structure de poutrelles qui suit le relief, la base est déjà à sa place quand la physique reprend → pas d'explosion.

**Le problème aujourd'hui : Parallax.** Le rendu est superbe et je veux le garder, mais les rochers qu'il sème au sol rendent la **construction impossible** : la grue ne peut plus aller où elle veut autour de la base.

D'où le besoin : **pouvoir nettoyer une zone (potentiellement grande) de ses rochers**, une fois, au moment de choisir l'emplacement d'une base.

## 2. Objectif du mod (nouveau)

**Supprimer les scatters dans un disque autour du rover, de façon persistante, sans déformer le relief.**

### Le levier : le PQSMod d'exclusion de Parallax

Parallax n'utilise **pas** le `removeScatter` du stock pour ses propres scatters : il embarque son PQSMod, `PQSMod_MapDecalVertexRemoveScatter` ([source](file:///c:/Users/Lionel/Desktop/kspmod-ext/Parallax-Continued/Mod%20Source/Parallax/PQS%20Mods/MapDecalVertexRemoveScatter.cs)). Ses propriétés, **lues dans le source puis confirmées en jeu** (voir §5) :

- **il ne touche jamais la hauteur** : pas de `OnVertexBuildHeight`, il ne déclare que `requirements = MeshColorChannel`. L'exclusion sans déformation est **native**, pas un détournement ;
- il croise **deux axes** : le **quoi** — une liste `blockedScatters` de **types** de scatters (jamais des instances) — et le **où** — un **masque `colorMap` (`Texture2D`)** projeté sur l'emprise du decal ;
- le masque est échantillonné **dans le compute shader** : canal **`.r`**, seuil **`< 0.5` = bloqué**, `linearClampSampler` ([`TerrainScatters.compute:176`](file:///c:/Users/Lionel/Desktop/kspmod-ext/Parallax-Continued/Assets/Shaders/Scatters/TerrainScatters.compute)).

Deux conséquences déterminantes :

- **La forme de la zone est libre.** `radius` ne définit qu'une **emprise maximale** ; à l'intérieur, c'est la texture qui décide, pixel par pixel. Cercle, carré, forme quelconque — au choix.
- **La finesse ne dépend pas du maillage PQS.** Le shader tourne **un thread par triangle**, tire un point candidat et échantillonne le masque **à la position de ce point** : la résolution effective est celle de la **texture**. Le grain de ~63 m entre sommets, qui condamnait l'objectif de nivellement (§3), **ne s'applique donc pas au nettoyage** — un disque de 20 m est parfaitement net (mesuré à 19,9 m en jeu, §5).

### Ce qu'on bloque

**Tous les types de scatters du corps.** Le but est une zone **visuellement nettoyée**, pas seulement praticable. On ne filtre donc pas sur le `collisionLevel` déclaré en config (0 = aucun collider → 4 ; sur la Mun : `TinyRocks`=0 … `HugeRocks`=4). Ce champ reste bon à connaître, mais ne sert pas de critère.

### Les deux rayons : l'emprise n'est PAS la zone nettoyée

Le PQSMod décide quels quads sont concernés en comparant sa position au **centre du quad** (`quad.positionPlanetRelative`, [`PQ.cs:388`](file:///d:/ksp-decompiled/PQ.cs)) :

```csharp
quadAngle = Acos(Dot(quad.positionPlanetRelative, normalisedPosition));
if (quadAngle < inclusionAngle) { maskedScattersPerQuad.Add(quad, this); }   // inclusionAngle = atan(radius/sphere.radius) * 4
```

Donner `radius = 20` m au decal serait donc une erreur : `inclusionAngle` vaudrait ~80 m au sol alors qu'un quad-feuille fait ~900 m d'arête, si bien que le centre du quad qui nous porte serait presque toujours hors de portée. **Aucun quad attaché, aucun effet — et aucune erreur dans le log.** Le symptôme serait identique au mur de l'ancien objectif (« le flatten à 10 m ne fait rien »), pour une cause totalement différente.

D'où le **découplage des deux rayons**, implémenté dans `ScatterMask.FootprintRadius` :

| | Rôle | Valeur |
|---|---|---|
| `radius` du PQSMod | **emprise** : capture les quads + définit l'espace UV du masque | calculée depuis la taille du quad — **325 m sur Kerbin**, 217 m sur la Mun |
| disque dessiné dans le masque | **la zone réellement nettoyée** | ce que demande le joueur (20 m) |

La résolution du masque se déduit des deux : l'emprise (2×`radius`) étalée sur la texture. Sur Kerbin, 650 m sur 1024 px ≈ **0,63 m/pixel** — assez fin pour que la bordure des 20 m soit franche, ce que le test confirme (§5).

### Le repère du masque : placer un point dans la texture

**L'espace couvert est un carré**, par construction : `u` et `v` sont calculés avec le *même* `radius`, donc la texture s'étale sur `2×radius` de côté (650 m chez nous sur Kerbin). Une texture non carrée serait **étirée**, pas rognée — d'où le 1024×1024.

**L'orientation vient du quaternion `rot`**, construit dans `OnSetup` et appliqué au point candidat par le shader :

```csharp
// C# (PQSMod_MapDecalVertexRemoveScatter.OnSetup)
rot = Quaternion.AngleAxis(angle, Vector3.up)                       // (2) réglage manuel
      * Quaternion.FromToRotation(normalisedPosition, Vector3.up);  // (1) amène le decal sur +Y
```
```hlsl
// GPU (TerrainScatters.compute)
float3 vertexRotation = QuaternionMul(_BlockMapRotation, avgDirection);
u = (vertexRotation.x * _PlanetRadius / _BlockMapRadius + 1) * 0.5;   // x -> u
v = (vertexRotation.z * _PlanetRadius / _BlockMapRadius + 1) * 0.5;   // z -> v
```

(1) bascule la direction du decal sur l'axe **Y**, ce qui fait du plan tangent au sol le plan **XZ** — ces deux axes deviennent U et V. (2) ajoute la rotation déclarée en config.

⚠️ **L'orientation par défaut n'est ni le nord ni rien de géographique** : `FromToRotation` prend la rotation minimale entre deux vecteurs — déterministe, mais **arbitraire, et dépendante de la latitude/longitude du decal**. C'est pourquoi Parallax a `angle = 15` en dur sur son decal du KSC : un calage manuel.

Sans conséquence tant que le masque est un **disque centré** (invariant par rotation), ce qui est le cas aujourd'hui. Mais **dès la fusion des masques** (plusieurs zones dessinées dans une même texture), il ne faudra pas deviner cette orientation : on rejoue la même transformation en C#, `rot` étant public.

```csharp
Vector3d dir = body.GetRelSurfaceNVector(lat, lon);   // le point à nettoyer
Vector3 v = decal.rot * (Vector3)dir;                 // le rot du decal, celui-là même qu'utilise le shader
double u = (v.x * body.Radius / decal.radius + 1.0) * 0.5;
double w = (v.z * body.Radius / decal.radius + 1.0) * 0.5;
// -> pixel (u * size, w * size), au flip vertical près selon la convention de SetPixels32 (à vérifier à l'implémentation)
```

### Contraintes

- **Zone circulaire**, rayon **20 m** (en dur pour l'instant, cf. « Ce qui reste à faire »), centrée sur le vaisseau.
- **Aucune modification du relief.** Choix structurant, pas une simplification : ne pas toucher à la hauteur supprime **par construction** toute la famille de problèmes qui a tué l'objectif précédent (§3) — pas de mesh de quad reconstruit différemment de ses voisins, donc ni trou ni fissure de LOD ; pas de sol qui monte ou descend sous un vaisseau posé, donc ni encastrement, ni explosion au rechargement, ni re-seating à gérer.
- **Persistant** : la zone nettoyée doit le rester après sauvegarde, rechargement, et redémarrage de KSP.
- **Application par sauvegarde + rechargement**, sur une **save dédiée** (`"BulldozerReload"`, jamais `"persistent"` : le rechargement est une manœuvre du mod, pas une sauvegarde voulue par le joueur, et un autosave de sortie de scène pourrait l'écraser). Le nettoyage n'a pas besoin d'être immédiat, et le rechargement garantit une injection **à froid**, avant que KSP ne bâtisse le PQS, vaisseau *packed*.
  Ici le rechargement est **inoffensif** : aucune hauteur n'étant modifiée, le rover réapparaît exactement à sa pose sauvegardée, sans risque d'encastrement ni d'explosion.

## 3. Pourquoi le nivellement est abandonné

Décision ferme, pas un report. Deux constats indépendants :

- **Le grain PQS interdit le gameplay visé.** La déformation ne peut déplacer que les sommets existants du maillage au niveau de subdivision max. Mesuré en jeu : Mun ~64 m, Kerbin ~100 m, Minmus ~114 m, Eve ~190 m entre deux sommets. « Niveler 10 m autour du rover » est **hors de portée du stock** ; on ne peut faire que de la terraformation de très grandes zones. *(À ne pas transposer au nouvel objectif : le nettoyage de scatters passe par une texture masque échantillonnée par triangle, il n'est donc pas limité par ce grain — voir §2.)*
- **Les effets de bord sont inhérents, pas des bugs de mon code.** Trous/fissures dans la map aux jonctions de LOD, vaisseaux qui explosent en réapparaissant dans la zone. **Vérifié avec Kerbal Konstructs**, qui pose aussi des zones plates : **mêmes effets de bord**. Ce n'est donc pas une question d'implémentation.

À quoi s'ajoute que, ma technique de poutrelles épousant le sol fonctionnant très bien, **je n'ai plus besoin de terrain plat du tout**. Seuls les rochers me gênent.

## 4. État du code

Le squelette de persistance et d'application « à froid » a été conservé ; tout ce qui touchait au relief est supprimé (récupérable dans git).

| Fichier | Rôle |
|---|---|
| `Src/BulldozerCleanModule.cs` | `PartModule` : entrée PAW **« Clear scatters »**, enregistre l'action puis save+reload. Posé sur tout module de commande par `GameData/BulldozerMod/BulldozerMod.cfg` (patch MM) — provisoire, la vraie pièce sera une lame |
| `Src/parallax/ParallaxInterop.cs` | Tout ce qu'on fait avec Parallax : pose du PQSMod d'exclusion, liste des scatters d'un corps, purge du registre statique. **Compilé contre `ParallaxContinued.dll`** (référence dans le `.csproj`) : une évolution de son API casse le build, pas le vol |
| `Src/terrain/ScatterCleaner.cs` | Purge nos decals (anti-fuite inter-saves) → injecte un decal par action → `ResetAndWait` des corps touchés |
| `Src/terrain/ScatterMask.cs` | Génère le masque (`Texture2D` 1024², disque noir sur fond blanc) **et** calcule l'emprise du decal à partir de la taille des quads (voir « Les deux rayons » au §2) |
| `Src/persistence/BulldozerAction.cs` | Une action `{ body, lat, lon, radius }` |
| `Src/persistence/BulldozerScenario.cs` | `ScenarioModule` (`AddToAllGames`) : source de vérité par save ; son `OnLoad` déclenche l'injection |

**Supprimés** : `BulldozerFlattenMod` (le PQSMod de nivellement), `TerrainLeveler`, `BulldozerPlugin` (bouton toolbar, remplacé par le PAW) et `BulldozerDiagnosticPlugin` (dump d'altitudes mesh vs analytique, sans objet maintenant).

Acquis transverses conservés : le `ScenarioModule` est la **source de vérité par save** (les corps sont `DontDestroyOnLoad` → une injection runtime **fuit entre parties** sans purge) ; on **n'agit jamais sur un PQS vivant**, on injecte pendant l'écran de chargement, vaisseau *packed* ; on ne **repositionne jamais** un vaisseau en physique active.

Question ouverte mineure : le nom « Bulldozer » n'est plus tout à fait juste (on ne pousse plus de terre, on ramasse les cailloux). À trancher plus tard — un renommage coûte un dossier `GameData`, un `.csproj`, un dépôt.

## 5. Ce qui est validé — et les deux pièges qui coûtaient cher

> ✅ **Le mécanisme fonctionne, vérifié en jeu le 2026-08-24 sur la Mun ET sur Kerbin.** Zone nettoyée visible autour du vaisseau, et le diagnostic confirme toute la chaîne — sur Kerbin, herbes, fougères, marguerites, roses et arbres lisent tous notre masque :
> ```
> maskedScattersPerQuad=45
> nearest scatter quad: sub=10 dist=483 m  inRegistry=YES  blockMapPQSMod=set  blockMapData=set
> scatters on that quad: 8, reading our block map: 8
>     Kerbin-KerbinGrass: readBlockMap=True
>     Kerbin-Daisy:       readBlockMap=True
>     Kerbin-Roses:       readBlockMap=True
>     Kerbin-OakTree:     readBlockMap=True   ...
> ```
> ✅ **Rayon vérifié à la mesure** : kerbal en EVA, vaisseau pris pour cible, la frontière de l'herbe haute tombe à **19,9 m** pour 20 m demandés. La bordure est franche — cohérent avec un masque à 0,63 m/pixel sur Kerbin (emprise 325 m étalée sur 1024 px).

Sont donc **acquis, en jeu et non plus seulement par lecture de source** : l'exclusion de scatters sans toucher au relief ; l'instanciation du PQSMod hors Kopernicus, injecté depuis `OnLoad` avant le build à froid ; le masque `Texture2D` **généré en code** et lu par le compute shader ; la liste complète des scatters d'un corps via `ConfigLoader.parallaxScatterBodies[...].fastScatters` ; la persistance par event-sourcing et le save+reload sur une save dédiée.

### PIÈGE 1 — `Math.Acos` renvoie NaN pour le quad qui compte

Parallax décide si un quad est concerné avec

```csharp
quadAngle = Math.Acos(Vector3d.Dot(quad.positionPlanetRelative, normalisedPosition));
if (quadAngle < inclusionAngle) { /* enregistre */ }
```

mais son champ `position` est un **`Vector3` (float, ~1e-7 de précision relative)** alors que `positionPlanetRelative` est un `Vector3d`. Quand le quad est **juste sous le decal**, l'angle réel est minuscule (8e-5 rad pour 16 m sur la Mun, soit un cosinus de `1 - 3.2e-9`, très en dessous du bruit du float) : le produit scalaire ressort **légèrement au-dessus de 1**, `Acos` rend **NaN**, et `NaN < inclusionAngle` est **faux** en C#.

Mesuré avant correction — le decal nettoyait une couronne et **ratait exactement son centre** :

| Quad | Distance | Enregistré |
|---|---|---|
| sous le rover | 16 m | ❌ NaN |
| 4 autres | 712–754 m | ✅ |

Parallax ne rencontre jamais ce cas : ses propres decals (le KSC) sont posés à des endroits arbitraires, jamais centrés au mètre près sur un quad. **Correctif** : `Src/parallax/BulldozerScatterDecal.cs`, une **sous-classe** de son PQSMod qui ne redéfinit que le test d'inclusion (clamp du produit scalaire avant `Acos`) et utilise la direction en **double précision**. Le registre `maskedScattersPerQuad` étant typé sur la classe de base, la sous-classe y est acceptée telle quelle.

⚠️ **Le même calcul existe côté GPU**, en float, dans [`TerrainScatters.compute:166`](file:///c:/Users/Lionel/Desktop/kspmod-ext/Parallax-Continued/Assets/Shaders/Scatters/TerrainScatters.compute) : tout point candidat à moins de ~70 m du centre du decal tombe dans la même zone de bruit. Ça n'a pas posé problème au premier essai, mais si des rochers isolés résistent près du centre, la cause est là — et le contournement est de **décaler le centre du decal** de quelques centaines de mètres (emprise plus large, disque dessiné hors-centre dans le masque).

### PIÈGE 2 — dériver d'un type Parallax exige `[KSPAssemblyDependency]`

Tant qu'on se contentait d'**utiliser** des types Parallax, rien à déclarer. Dès qu'on **hérite** de l'un d'eux, KSP doit charger `ParallaxContinued` pour construire notre type — et il chargeait notre DLL **avant** (55.741 contre 55.928), sans savoir où chercher :

```
AssemblyLoader: Exception loading 'BulldozerMod': ReflectionTypeLoadException
  System.IO.FileNotFoundException: Could not load file or assembly 'ParallaxContinued, Version=1.0.0.0'
```

Le mod ne se chargeait pas du tout (KSPCommunityFixes l'affiche en clair au démarrage). **Correctif** dans `Src/AssemblyInfo.cs` :

```csharp
[assembly: KSPAssembly("BulldozerMod", 1, 0)]
[assembly: KSPAssemblyDependency("Parallax", 1, 0)]
```

Le nom déclaré est **`Parallax`** — celui du `[KSPAssembly]` de Parallax, pas son nom de fichier (voir aussi le piège `LoadedAssembly.name` dans le [CLAUDE.md parent](../CLAUDE.md)). Effet de bord souhaitable : sans Parallax, KSP écarte proprement le mod au lieu de planter.

### Deux limites observées en jeu

**Près du KSC, rien ne se nettoie.** Parallax y pose son propre decal, `ParallaxMaskKSC` (`radius = 1400` m, donc une portée de `atan(1400/600000) × 4` ≈ **5,6 km**), et un quad n'accepte qu'un seul decal : le nôtre est ignoré sur tous les quads déjà pris, silencieusement. Le log le nomme désormais explicitement (`Quad already owned by 'ParallaxMaskKSC'`). Sans conséquence pour l'usage visé — on ne construit pas de base sur le KSC — mais à savoir pour ne pas croire à une panne.

**Le nettoyage peut être partiel selon l'instant.** Un essai a laissé fleurs et herbes éparses ; le diagnostic montrait alors que le quad portant le vaisseau **n'était pas** pris en charge (le plus proche connu de Parallax était à 752 m, au-delà des 651 m possibles pour un quad de 920 m d'arête). Les scatters de ce quad avaient donc été générés sans notre masque. Rejoué au même endroit, le résultat était parfait. **Piste** : un quad ne s'enregistre qu'à sa construction (`isBuilt` n'est remis à `false` que dans `Awake()`), donc tout quad déjà bâti au moment de l'injection est perdu pour la passe. À surveiller si le cas se reproduit.

## 6. Ce qui reste à faire

- **Plusieurs nettoyages par partie** : aujourd'hui un seul, car deux decals se disputeraient les mêmes quads (voir plus bas). Il faut **fusionner dans un seul decal par chantier** et enrichir son masque à chaque passe.
- **Rayon paramétrable** côté joueur, puis la **pièce dédiée** (lame lourde, consommable après une maille) à la place du patch sur les modules de commande.
- **Retirer le diagnostic** `Src/diagnostics/BulldozerScatterDiagnostic.cs` — le rayon est validé, il ne sert plus qu'en cas de régression.
- **Détail terrain : exigence probablement caduque, à confirmer.** On voulait imposer le preset maximum, d'abord pour la finesse (elle vient de la texture, pas du maillage : sans objet), puis parce que la taille des quads en dépend. Or `ScatterMask.FootprintRadius` calcule déjà l'emprise depuis `sphere.maxLevel` **réel**, donc le mod s'adapte au preset ; et les actions étant stockées en lat/lon absolus, le rejeu ne dépend pas du maillage. Reste à vérifier qu'aucune autre dépendance ne subsiste — si non, l'exigence tombe (levier si besoin : `PQSCache.PresetList.presetIndex == PQSCache.PresetList.presets.Count - 1`).
- **Bords de zone** : coupure franche ou dégradé dans le masque (le sampler est bilinéaire, un masque adouci suffirait).

### Contraintes toujours valables

**Un seul decal par quad.** Le refus est explicite côté Parallax (« *Multiple MapDecalVertexRemoveScatter mods on the same quad! You must combine these decals into one* ») et **silencieux** : le second decal est simplement ignoré sur les quads déjà pris. Comme l'emprise vaut 4× le rayon et se compare au **centre** des quads, deux decals se gênent jusqu'à ~2,6 km d'écart sur Kerbin. D'où la fusion à coder avant tout second nettoyage.

**Le rayon du decal n'est pas le rayon nettoyé** (voir §2) : l'emprise doit rester calibrée sur la taille des quads.

### Restant ouvert
- **Les scatters stock** (`removeScatter` → `vbData.allowScatter`) ne sont pas traités. Parallax respecte ce flag, donc un `PQSMod_MapDecal` stock serait une voie complémentaire — sans objet tant que Parallax est installé.
- **Effets de bord visuels** : le decal déclare `MeshColorChannel`, d'où la question d'un impact éventuel sur la couleur du terrain et sur les biomes. Rien de visible aux essais.
