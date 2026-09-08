# État du code : carte des fichiers et outils de debug

Ce que contient chaque fichier source, et l'outillage de diagnostic en jeu (fenêtre de debug, relevé de limite de quad).

> Fichier de contexte de **KSP-BulldozerMod**. Le fil directeur du projet est dans [CLAUDE.md](CLAUDE.md).

## État du code

Le squelette de persistance et d'application « à froid » a été conservé ; tout ce qui touchait au relief est supprimé (récupérable dans git).

| Fichier | Rôle |
|---|---|
| `Src/BulldozerCleanModule.cs` | Le `PartModule` des trois lames : entrée PAW **« Passer la lame »**, curseurs **longueur** (5→200 m) et **largeur** (5→20 m) par pas de 5 m, **usure restante**, **coût de la passe** — remplacé par **la zone qui réserve le terrain** quand un autre mod tient un des quads visés —, **hauteur au sol** et **inclinaison** en direct. Construit la bande, relève les quads impactés, applique les règles de refus **et le quota**, **demande confirmation**, puis débite la lame, enregistre l'action et save+reload. `TryBuildBand` ne prend qu'une **lame** — origine = son tranchant, direction = son `bladeFace` — et c'est le **seul** chemin : les calques l'appellent aussi, avec la lame en aperçu, pour viser la bande exacte du clic |
| `Src/terrain/PassCost.cs` | Ce qu'une passe coûte : les cases qu'elle ferait passer de blanc à noir, **chacune imputée au seul quad qui la possède**. Repasser sur du propre est gratuit ; nettoyer un terrain sans cailloux est plein tarif |
| `GameData/BulldozerMod/Parts/*.cfg` | Les trois lames (BD-2 / BD-3 / BD-5), branchées sur leurs propres modèles, et leur `ModulePartVariants` : **deux livrées** pour une seule entrée dans la liste de l'éditeur |
| `GameData/BulldozerMod/Parts/*.mu` | Les maillages, **produits de build** : ne jamais les éditer, relancer le générateur |
| `tools/make_blades.py` | Génère les trois modèles et les exporte en `.mu`. Toute la géométrie dérive de la **largeur** ; c'est aussi le seul endroit où vivent la convention d'axes Blender→Unity, la garantie « rien sous le point d'attache », et les **deux livrées** — celle que le `.mu` embarque et celle qui n'existe qu'en `.cfg`, dont il imprime le bloc |
| `tools/render_blades.py` | Rend les trois lames en PNG en **important** le générateur — donc jamais une seconde description de la forme **ni de la peinture**. Une image par vue et par livrée : c'est le seul moyen de voir une livrée qui n'existe qu'en `.cfg` sans installer le mod. Dessine aussi le **sol** et une **charnière fictive à plat** plantée dans le nœud haut — les deux ensemble répondent à la seule question que ce nœud pose : bras à l'horizontale, le tranchant touche-t-il, et le raccord est-il droit ? `--blade` isole une lame et débloque les vues de **profil strict** et de 3/4 arrière, seules capables de le montrer. Cadrage et focale **ajustés** à la géométrie, pour que les vues ne sortent pas du champ en silence quand les constantes changent. Sort dans `tools/renders/`, **non versionné** : ce sont des rendus de contrôle, à régénérer, pas du contenu de dépôt |
| `Src/parallax/ParallaxInterop.cs` | Tout ce qu'on fait avec Parallax : pose du decal, liste des scatters d'un corps, purge du registre statique. **Compilé contre `ParallaxContinued.dll`** : une évolution de son API casse le build, pas le vol |
| `Src/parallax/BulldozerScatterDecal.cs` | Le decal lui-même : sous-classe du PQSMod de Parallax, **attachée à un seul quad** (celui dont elle occupe le centre). `RotationFor` = source unique du `rot` du masque |
| `Src/parallax/ScatterHighlighter.cs` | L'aperçu, et **la seule chose qui dit la vérité** sur ce qu'une passe enlèvera. Porte `Source`, la lame en cours de prévisualisation — **une seule à la fois** (coût), et c'est elle que tous les calques visent : pour chaque quad en portée il construit le masque que l'injection construirait (`ScatterCleaner.RasterFor`) et pose au shader sa propre question (bilinéaire, `< 0.5`) à la position de chaque instance. Montre donc la pixelisation, les décrochements de couture et les quads pris par un autre mod. Ne voit que les scatters **collidables** — l'herbe part aussi, sans jamais avoir été surlignée, et seulement ceux que Parallax a **chargés** : c'est `BandTrace` qui couvre le reste de la bande |
| `Src/parallax/BandTrace.cs` | **La zone, peinte au sol** : les cases que la passe noircirait, quad par quad. C'est ce qui montre le **bout** d'une passe de 200 m, là où le surlignage des rochers ne peut structurellement rien montrer. Dessine les **cases**, jamais le rectangle — donc la pixelisation, les coutures, et **en ambre** la portion posée sur un quad réservé par un autre mod. Ne porte plus que le **quoi** et le **quand** : la géométrie est dans `GroundSheet` |
| `Src/parallax/GroundSheet.cs` | **La géométrie de tout calque au sol** : mesurer des cases au raycast, les mailler, les dessiner dans le repère du corps. Partagée par le tracé de la passe et par les zones vertes, pour qu'ils ne puissent pas dessiner le même sol différemment. La sonde part de la hauteur **analytique du point mesuré**, pas d'une altitude unique pour la feuille : une feuille couvre un quad entier, sur lequel le relief dépasse la portée du rayon |
| `Src/parallax/CleanedZonesOverlay.cs` | **Les zones déjà nettoyées, en vert** (case à cocher de la fenêtre de debug). Lit les **masques vivants**, jamais les actions enregistrées : c'est là qu'est la valeur de debug — une action écartée à l'injection ou un quad pris par un autre mod se voient comme un **trou dans le vert**. Une feuille par quad, construite une fois puis gardée (une zone ne bouge pas), un seul quad maillé par frame |
| `Src/parallax/OverlayMaterial.cs` | Le matériau des deux aperçus, et **le seul endroit** où vit la liste de shaders : un shader de KSP transforme un calque en artefact d'espace écran ([CLAUDE-validation.md](CLAUDE-validation.md)), et `Shader.Find` échoue en silence |
| `Src/terrain/BladeStance.cs` | Comment la lame est posée : hauteur du **tranchant** au-dessus du sol et angle de son axe **haut** avec la **normale du sol**, en un raycast. Mesuré contre le sol et non contre l'horizon — sur une pente, une lame bien posée suit la pente |
| `Src/terrain/SurfaceBand.cs` | La bande d'une passe : origine (le tranchant de la lame), direction, longueur, largeur — **tout en directions unitaires du repère du corps**, jamais de lat/lon. Porte `WidthStep` (= une case), le pas et le minimum des deux curseurs |
| `Src/terrain/MaskProjection.cs` | **Le repère d'un masque**, et rien d'autre : direction du sol ⇄ pixel, dans l'expression exacte du compute shader. Séparé de `BandRaster` parce que deux choses en ont besoin — une bande qu'on **prévoit**, et un masque qui **existe déjà** et qu'on relit |
| `Src/terrain/BandRaster.cs` | **Le cœur de la pixelisation** : la bande projetée dans le repère d'UN masque, la règle « toute case touchée », et l'échantillonnage bilinéaire + seuil `< 0.5` du shader. Une seule implémentation, utilisée par le tracé **et** par le highlight — deux versions feraient mentir l'aperçu |
| `Src/terrain/QuadLocator.cs` | Géométrie des quads : arête, demi-diagonale, et **quels quads-feuilles une passe touche** (en vol uniquement) : pré-sélection par calotte contenant la bande, puis test **exact** « au moins une case effacée ». Donne aussi le **premier quad pris par un autre mod**, un seul suffisant à refuser toute la passe |
| `Src/terrain/ScatterMask.cs` | Le masque d'un decal (`Texture2D` R8 dimensionnée à 5 m/case) ; `DrawBand` sert aussi bien à créer qu'à fusionner |
| `Src/terrain/ScatterCleaner.cs` | Purge nos decals, **regroupe les passes par quad** (un decal chacun), `ResetAndWait` des corps touchés. `RasterFor` = point d'entrée unique pour « le masque que ce quad recevrait » |
| `Src/persistence/BulldozerAction.cs` | Une action `{ body, lat, lon, forward, length, width, maxLevel, QUAD*{ dir, halfDiag } }`. La **largeur y voyage** : rejouer une vieille passe doit utiliser la largeur avec laquelle elle a été demandée, pas celle du curseur au moment du chargement |
| `Src/persistence/BulldozerScenario.cs` | `ScenarioModule` (`AddToAllGames`) : source de vérité par save ; son `OnLoad` déclenche l'injection |
| `Src/ui/BulldozerDebugUI.cs` | **Le bouton de la barre d'app (vol uniquement) et la fenêtre qu'il ouvre.** Vol uniquement parce que ce que la fenêtre commute se dessine sur du terrain qui n'existe qu'en vol |
| `Src/ui/BulldozerDebugViewModel.cs` | L'état de la fenêtre, et le **seul** endroit où un réglage persisté et l'état statique qu'il pilote sont tenus en phase (calque, bouton de diagnostic, niveau de log) — ces états doivent être statiques, leur addon étant reconstruit à chaque changement de scène |
| `Src/ui/ugui/**`, `Src/ui/styles/` | Les composants uGUI, un `Builder` + un `Controller` chacun (pattern `KSP-Shared`). `QuadReadout*` est le seul qui ne pilote **aucun** réglage : il lit le monde, se rafraîchit tout seul 2×/s, et n'est donc pas passé au `ContentController` |
| `Src/settings/BulldozerSettings.cs` | Les réglages **par installation** (`PluginData/bulldozer.cfg`) — jamais des données de partie : les zones nettoyées, elles, sont dans le `ScenarioModule` |
| `Src/Mod.cs` | L'addon de démarrage (`PSystemSpawn`, une fois par session) : il ne fait qu'appliquer le **niveau de log** persisté, avant que quoi que ce soit ait logué |
| `tools/create_icon.ps1` | Génère `GameData/BulldozerMod/icon.png` (38 px, silhouette blanche sur fond transparent, au style des mods frères). Produit de build : ne pas retoucher le PNG, relancer le script |
| `Src/diagnostics/QuadReadout.cs` | **Outil de test** : le texte des quads voisins, affiché par la fenêtre de debug — voir ci-dessous |
| `Src/diagnostics/BandDiagnostic.cs` | **Outil de test** (entrée PAW « Analyser la passe ») : dump lecture seule de tout ce qu'une passe déciderait. Conçu pour séparer quatre pannes qui se ressemblent à l'écran — projection fausse, familles non surlignables, une lame étroite face à des grappes, ou un decal que Parallax n'a jamais câblé. C'est lui qui a prouvé la visée ([CLAUDE-validation.md](CLAUDE-validation.md)) et permis de chiffrer le bug du cap |

### Se placer sur une limite de quad (le relevé de la fenêtre de debug)

**Dans la fenêtre de debug** (bouton de la barre d'app, en vol), sous les réglages. Il liste les **4 quads-feuilles les plus proches**, avec la distance du vaisseau à leur **centre** et leur propriétaire éventuel :

- deux distances égales ⇒ le vaisseau est sur **l'arête** entre ces deux quads ;
- quatre distances égales ⇒ il est sur le **coin** où quatre quads se rejoignent ;
- la ligne `spread` donne directement l'écart entre la plus petite et la plus grande — la faire tendre vers 0 revient à se placer au coin.

La dernière ligne annonce combien de quads la passe de la **lame en aperçu** toucherait à cet endroit : c'est le nombre de decals qui seraient créés, donc la valeur à surveiller pour tester une passe à cheval. Sans aperçu actif, il n'y a pas de passe à décrire et la ligne le dit — allumer la prévisualisation d'une lame.

⚠️ **C'était un `KSPAddon` IMGUI à part, ouvert par MOD+B, jusqu'au 2026-09-02.** Il est passé dans la fenêtre de debug pour une raison de coût, et c'est la leçon à retenir : **Unity appelle `OnGUI` plusieurs fois par frame** (Layout, Repaint, events) sur tout `MonoBehaviour` qui le définit, avec le marshalling IMGUI qui va avec — un `return` en tête de méthode sur un booléen « pas visible » ne rachète rien. Un outil de debug ne doit donc jamais définir `OnGUI` : hébergé dans la fenêtre uGUI, il ne coûte **rien du tout** tant qu'elle est fermée, `PopupController.Hide()` désactivant le GameObject de la fenêtre (donc l'`Update` du relevé avec).

Le relevé se recalcule **2×/s tant que la fenêtre est ouverte** — le rythme du coût de passe dans le PAW. Le parcours du registre de quads de Parallax est donc payé une fois de plus par rafraîchissement quand la fenêtre est ouverte ; c'est le prix assumé de l'ouvrir, et le `FindImpactedQuads` (la partie chère) n'est fait **que** si une lame est en aperçu.

⚠️ Le texte du relevé reste **en anglais** alors que la fenêtre autour est en français : c'est un dump de développeur, du même registre que ceux de `BandDiagnostic` dans `KSP.log`, et la moitié de ses mots (`half-diag`, `spread`) ne se nomment qu'en anglais dans ce projet. Seule la légende au-dessus est traduite (`debugQuads`).

**Supprimés** : `BulldozerFlattenMod` (le PQSMod de nivellement), `TerrainLeveler`, `BulldozerPlugin` (bouton toolbar, remplacé par le PAW), `BulldozerDiagnosticPlugin` (dump d'altitudes mesh vs analytique, sans objet maintenant) et `QuadOverlay` (l'addon IMGUI ci-dessus, devenu une section de la fenêtre de debug).

### Un seul diagnostic, à la demande (2026-09-02)

`BulldozerScatterDiagnostic` — un `KSPAddon` de vol qui dumpait la chaîne 8 s après **chaque** entrée en vol — est **supprimé**, et ses deux sections irremplaçables sont devenues une section de `BandDiagnostic` (`DumpWiring`).

**Ce qu'il coûtait** : sa coroutine partait **sans condition**, en partie normale, et son dump est écrit en `LogInfo` — donc visible au niveau par défaut. Un tri de tout `ScatterComponent.scatterQuadData` et ~14 lignes de log à chaque chargement de vol, pour un outil que le joueur n'a pas demandé, alors que l'autre diagnostic sait déjà se cacher derrière une case à cocher.

**Ce qu'il fallait sauver, et pourquoi le reste du dump ne le remplace pas.** Tout le reste de `BandDiagnostic` est **notre propre arithmétique rejouée** — la ligne `owner=`, le raster, les verdicts d'instances. Ils sont d'accord entre eux par construction, donc ils ne peuvent pas attraper la panne où le decal est attaché, le masque est bon, et les cailloux restent quand même. Deux lignes seules lisent l'état du **jeu** :

- **`readBlockMap` par famille** — la preuve que *Parallax* a câblé notre masque dans les données de scatters du quad. C'est la ligne que le [CLAUDE-validation.md](CLAUDE-validation.md) cite comme preuve de la chaîne ;
- **le quad absent du registre** — `FindImpactedQuads` parcourt ce même registre, donc un quad manquant **sort de la liste sans un mot**. Il se lit ici comme un quad le plus proche plus loin qu'une demi-diagonale, exactement le cas de nettoyage partiel resté ouvert en [CLAUDE-validation.md](CLAUDE-validation.md).

**Ce qui a été abandonné au passage**, parce que déjà couvert : le classement des quads voisins et la mesure de forme (c'est `QuadReadout`, dans la fenêtre de debug, sur la même source) ; la liste des quads enregistrés (c'est le vert de `CleanedZonesOverlay`) ; et le test « ce quad passerait-il l'inclusion maintenant ? », outil de l'enquête `Acos`→NaN (PIÈGE 1, corrigé depuis dans notre sous-classe, et dont `owner=` répond à la même question).

⚠️ **Le réflexe à ne pas avoir** : remettre un addon de vol pour rejouer ce dump automatiquement après un rechargement. Ce que ça achèterait — ne pas cliquer — ne vaut pas une coroutine dans chaque partie ; la passe qui vient d'être appliquée a laissé une lame sur le rover, donc l'entrée PAW est toujours à portée.

Acquis transverses conservés : le `ScenarioModule` est la **source de vérité par save** (les corps sont `DontDestroyOnLoad` → une injection runtime **fuit entre parties** sans purge) ; on **n'agit jamais sur un PQS vivant**, on injecte pendant l'écran de chargement, vaisseau *packed* ; on ne **repositionne jamais** un vaisseau en physique active.

Question ouverte mineure : le nom « Bulldozer » n'est plus tout à fait juste (on ne pousse plus de terre, on ramasse les cailloux). À trancher plus tard — un renommage coûte un dossier `GameData`, un `.csproj`, un dépôt.

