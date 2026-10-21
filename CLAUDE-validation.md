# Ce qui est validé en jeu — et les pièges qui coûtaient cher

Les vérifications faites en jeu (Mune, Gilly, Eve) avec leurs mesures, et les pièges déjà payés : NaN d'`Math.Acos`, `[KSPAssemblyDependency]`, injection hors vol, ombres fantômes du surlignage.

> Fichier de contexte de **KSP-Mod-Bulldozer**. Le fil directeur du projet est dans [CLAUDE.md](CLAUDE.md).

## Ce qui est validé — et les pièges qui coûtaient cher

> Un troisième piège, découvert le 2026-08-27, est documenté ailleurs parce qu'il vit dans une règle de refus : **la projection horizontale d'un axe de contrôle vertical n'est pas un cap**. Voir [CLAUDE-regles-passe.md](CLAUDE-regles-passe.md).

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
> ✅ **Architecture « un decal par quad » validée le 2026-08-24** : nettoyages multiples dans une même partie, y compris à cheval sur quatre quads, chacun recevant son decal et sa portion de zone. La fusion (deux zones sur un même quad) passe par le même chemin.
> ✅ **Rayon vérifié à la mesure** : kerbal en EVA, vaisseau pris pour cible, la frontière de l'herbe haute tombe à **19,9 m** pour 20 m demandés. La bordure est franche — cohérent avec un masque à 0,63 m/pixel sur Kerbin (emprise 325 m étalée sur 1024 px).

Sont donc **acquis, en jeu et non plus seulement par lecture de source** : l'exclusion de scatters sans toucher au relief ; l'instanciation du PQSMod hors Kopernicus, injecté depuis `OnLoad` avant le build à froid ; le masque `Texture2D` **généré en code** et lu par le compute shader ; la liste complète des scatters d'un corps via `ConfigLoader.parallaxScatterBodies[...].fastScatters` ; la persistance par event-sourcing et le save+reload sur une save dédiée.

### ✅ Vérifié en jeu le 2026-08-27 (Mune) : le masque de la bande est exact

Dump `BandDiagnostic` en main, rover immobile, `maxLevel=9`, quads 728 × 754 m, 4 masques de 231 px à 4,99 m/px :

- `origin check: lat/lon vs world position differ by **0,11 m**` → l'origine de la bande est bien sous le rover, malgré l'aller-retour lat/lon (⚠️ **cette ligne a changé de référence le 2026-08-31** : la passe partant désormais du **tranchant de la lame**, le dump compare au tranchant et non plus au centre du vaisseau, et affiche l'écart des deux sur une ligne à part — un `origin check` à 0 et un déport de quelques mètres sont donc le résultat **attendu**, pas une régression) ;
- `quad center -> px=(115.0,115.0) expected (115.0,115.0)` sur **les 4 quads** → le repère du masque (footprint, taille, `rot`) est reproduit exactement ;
- la table d'instances suit la géométrie au demi-mètre : `perp=0.2 → BLOCKED`, `perp=-1.4 → BLOCKED`, `perp=28.0 → kept` ;
- les cas limites **confirment en jeu** le modèle mesuré hors du jeu : `perp=5.9 → mask=0.31 BLOCKED` mais `perp=5.7 → mask=0.55 kept`, soit un couloir effectif qui atteint ~±6 m par endroits.

**Combien une passe retire, et pourquoi le compte trompe.** Une passe de **200 × 5 m** ne bloque que **7 instances sur les ~141** du voisinage (13 sur 152 dans une seconde mesure) : la lame passe littéralement **entre** les ancres. C'est ce chiffre qui a motivé la largeur réglable.

⚠️ **Mais le compte d'instances sous-estime largement le résultat visuel**, et c'est la règle des grappes qui joue cette fois dans le bon sens : une instance retirée est une **grappe** dont le maillage s'étale sur 15-25 m. Attraper 7 ancres dégage donc un couloir de plusieurs dizaines de mètres à l'œil — vérifié sur les captures avant/après de la passe 200 × 5 m. La bonne métrique est « la largeur de sol qui paraît propre », pas le nombre d'instances. À juger en jeu avec le curseur, pas sur le log.

**La visée est PROUVÉE, pas supposée.** Dump pris en roulant à 1,5 m/s :

```
band forward                                             224.9 deg
control point .up (ce que la bande utilise)              224.9 deg
vessel root .forward                                     224.9 deg
SURFACE VELOCITY (1.5 m/s) = où le rover va vraiment     224.8 deg
```

0,1° d'écart avec la direction réelle de déplacement, et deux axes indépendants d'accord. Toute hypothèse de « bande décalée » est close.

**La chaîne complète est validée** (2026-08-27, Mune, passe 200 × 5 m) : `Recorded pass … over 4 quad(s)` → `Injected 4 decal(s) carrying 4 stroke(s); reset 1 body(ies)` → après rechargement, `owner=BulldozerScatterDecal` sur les 4 quads et **`0 would go`** sur 112 instances à portée : plus rien de collidable sous la bande. Confirmé visuellement (couloir dégagé).

**Le seuil de cap, chiffré.** Le même dump donne `vessel root .up` à fraction verticale **0,984** — c'est l'axe du point de contrôle « Haut ». Sa composante horizontale vaut `√(1−0,984²) = 0,178`, soit **5 % au-dessus de l'ancien seuil de 0,17** : il passait. Son cap valait **79,5°** (145° à côté du rover, donc bande hors champ) et « Inversé » **259,5°** (34,6° à droite du rover). C'est exactement le symptôme observé, au degré près — voir le piège dans [CLAUDE-regles-passe.md](CLAUDE-regles-passe.md).

**Et deux familles échappent structurellement à l'aperçu** : sur la Mune, `Mun-TinyRocks` et `Mun-SmallRocks` ne sont **pas collidables**. Elles sont bien retirées par le nettoyage, mais `CollisionManager.collisionData` ne les décrit pas instance par instance, donc le highlight ne peut pas les montrer. `MedRocks`, `LargeRocks`, `HugeRocks`, `HugeRocks2`, `HugeRocks41` le sont.

### ✅ Vérifié en jeu le 2026-08-27 (Gilly) : le dimensionnement tient au petit bout

Second corps, choisi pour être le **plus petit du système** — donc le cas où la texture est la plus petite et où la bande déborde le plus. Vaisseau sans roues (inutiles à 0,049 m/s²), posé sur des réservoirs RCS. `maxLevel=7`, quads 189 × 196 m, **3 masques de 60 px à 4,98–5,01 m/px** :

- `quad center -> px=(30.0,30.0) expected (30.0,30.0)` sur **les 3 quads** → le repère du masque est exact sur un corps et une taille de texture entièrement différents de la Mune (60 px contre 231) ;
- `band forward 111,8°` contre **112° au navball** → la visée reste bonne avec un point de contrôle « suivant » ;
- **la troncature est exercée pour la première fois** : `band px start=(-10.2,35.5) end=(26.7,19.5)` sur un masque de 60 px — la bande sort des **deux** côtés et le clamp fait son travail (`cells cleared=67 in box [0..28]x[18..37]`). Ce chemin n'avait jamais été emprunté sur la Mune, où une passe de 200 m tient dans l'emprise d'un quad ;
- chaîne complète : `Recorded pass … over 3 quad(s)` → `Injected 3 decal(s) carrying 3 stroke(s)` (4 familles bloquées par decal) → après rechargement, `owner=BulldozerScatterDecal` sur les 3 quads et **`0 would go`** sur 782 instances à portée.

C'est ce test qui a montré que **`maxLevel` varie par corps** (voir « Taille du masque ») ; la conclusion pratique est que le plancher de 32 px est **hors de portée du stock**.

⚠️ Détail à ne pas prendre pour un bug : entre l'injection et le dump de contrôle, un quad affichait 66 cases puis 67. Le dump recalcule la bande depuis la position **courante** du vaisseau, l'injection rejoue celle **enregistrée** — quelques centimètres de dérive suffisent à faire basculer une case de bord.

### ✅ Vérifié en jeu le 2026-08-27 (Eve) : et au gros bout aussi

Troisième corps, choisi cette fois pour être le **plus gros corps rocheux** — le cas où le masque est le plus lourd. `maxLevel=10`, quads 1271 × 1318 m, **2 masques de 403 px à 5,00 m/px** (316 Ko pour la passe) :

- `quad center -> px=(201.5,201.5) expected (201.5,201.5)` sur les 2 quads → le repère est exact sur la troisième taille de masque testée (60 / 231 / 403 px) ;
- `band forward 88,6°` contre **089° au navball** ;
- le rover étant sur une pente, `control point .up` a une fraction verticale de **-0,139** — accepté, et loin du seuil de refus (0,707). Le garde-fou ne se déclenche pas sur un simple dévers, ce qui était l'inquiétude après le bug de cap du même jour ;
- **403 px pour un plafond à 2048** : la marge est telle que `MaxSize` ne peut pas être atteint en stock.

Seule l'analyse a été faite ici (`owner=free`, aucune passe appliquée) : la chaîne d'injection étant déjà validée sur la Mune et sur Gilly, ce test ne visait que le dimensionnement.

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

⚠️ **Le même calcul existe côté GPU**, en float, dans [`TerrainScatters.compute:166`](file:///d:/kspmod-ext/Parallax-Continued/Assets/Shaders/Scatters/TerrainScatters.compute) : tout point candidat à moins de ~70 m du centre du decal tombe dans la même zone de bruit. Ça n'a pas posé problème au premier essai, mais si des rochers isolés résistent près du centre, la cause est là — et le contournement est de **décaler le centre du decal** de quelques centaines de mètres (emprise plus large, zone dessinée hors-centre dans le masque).

### PIÈGE 2 — dériver d'un type Parallax exige `[KSPAssemblyDependency]`

Tant qu'on se contentait d'**utiliser** des types Parallax, rien à déclarer. Dès qu'on **hérite** de l'un d'eux, KSP doit charger `ParallaxContinued` pour construire notre type — et il chargeait notre DLL **avant** (55.741 contre 55.928), sans savoir où chercher :

```
AssemblyLoader: Exception loading 'KSPModBulldozer': ReflectionTypeLoadException
  System.IO.FileNotFoundException: Could not load file or assembly 'ParallaxContinued, Version=1.0.0.0'
```

Le mod ne se chargeait pas du tout (KSPCommunityFixes l'affiche en clair au démarrage). **Correctif** dans `Src/AssemblyInfo.cs` :

```csharp
[assembly: KSPAssembly("KSPModBulldozer", 1, 0)]
[assembly: KSPAssemblyDependency("Parallax", 1, 0)]
```

Le nom déclaré est **`Parallax`** — celui du `[KSPAssembly]` de Parallax, pas son nom de fichier (voir aussi le piège `LoadedAssembly.name` dans le [CLAUDE.md parent](../CLAUDE.md)). Effet de bord souhaitable : sans Parallax, KSP écarte proprement le mod au lieu de planter.

### Limites observées en jeu

**Près du KSC, rien ne se nettoie.** Parallax y pose son propre decal, `ParallaxMaskKSC` (`radius = 1400` m, donc une portée de `atan(1400/600000) × 4` ≈ **5,6 km**), et un quad n'accepte qu'un seul decal : le nôtre est ignoré sur tous les quads déjà pris, silencieusement. Le log le nomme explicitement (`Quad already owned by 'ParallaxMaskKSC'`). Sans conséquence pour l'usage visé — on ne construit pas de base sur le KSC — mais à savoir pour ne pas croire à une panne.

**Le nettoyage peut être partiel selon l'instant.** Un essai a laissé fleurs et herbes éparses ; le diagnostic montrait alors que le quad portant le vaisseau **n'était pas** pris en charge (le plus proche connu de Parallax était à 752 m, au-delà des 651 m possibles pour un quad de 920 m d'arête). Les scatters de ce quad avaient donc été générés sans notre masque. Rejoué au même endroit, le résultat était parfait. **Piste** : un quad ne s'enregistre qu'à sa construction (`isBuilt` n'est remis à `false` que dans `Awake()`), donc tout quad déjà bâti au moment de l'injection est perdu pour la passe. À surveiller si le cas se reproduit.

### PIÈGE 4 — injecter hors du vol casse les bâtiments du KSC

Constaté le 2026-08-31 : dans la vue du Centre spatial, **la station de suivi et la R&D ne sont plus cliquables**, alors que la piste et le SPH le restent. Désinstaller le mod le fait disparaître, le réinstaller le fait revenir.

Ce n'est **ni le decal du KSC, ni la prévisu** — deux fausses pistes naturelles, écartées par la mesure :

- **la prévisu est en vol uniquement** (`ScatterHighlighter`, `BandTrace`, `CleanedZonesOverlay` et la fenêtre de debug sont tous `KSPAddon.Startup.Flight`) : rien de tout ça n'existe au KSC ;
- **nos decals Kerbin étaient à 15 et 22 km à l'ouest du KSC** (relevés dans `KSP.log` : `dir=[0.2416, 0.0009, -0.9703]` et `[0.2309, 0.0047, -0.9730]`, soit lon ≈ −76,0° et −76,65° contre −74,56° pour le KSC) — très au-delà de la portée de `ParallaxMaskKSC`. Aucune rencontre possible entre les deux ;
- **aucune exception** dans le log pendant le chargement de la scène.

La vraie cause est ailleurs, et le log la donne sans ambiguïté : `BulldozerScenario` est déclaré pour `FLIGHT | SPACECENTER | TRACKSTATION`, donc **son `OnLoad` injectait aussi au Centre spatial** — et l'injection se termine par `ResetAndWait()` sur **chaque corps touché, dont Kerbin**. On rebâtissait donc la sphère de Kerbin pendant le chargement du KSC :

```
16:24:26.057 [Scenario] Loaded 10 action(s)
16:24:26.103 Update Quads Init: Kerbin          <- notre ResetAndWait
16:24:29.656 [ScatterCleaner] Injected 6 decal(s) ...; reset 2 body(ies)
16:24:30.160 [ApplicationLauncher] scene SPACECENTER
```

La corrélation est nette : avec **0 action**, `OnLoad` dure 4 ms et ne reset rien — et le bug ne se produit pas ; avec 10 actions, il dure **3,5 s** et reset Kerbin.

Le mécanisme fin n'est pas prouvé, mais il n'a pas besoin de l'être pour trancher, et le sens de recherche est connu : un bâtiment du KSC se clique par `OnMouseOver` d'Unity (`SpaceCenterBuildingCollider`, qui appelle `SpaceCenterBuilding.ColliderHover`), donc par un **raycast physique où seul le collider le plus proche reçoit le message**. Tout ce qui se glisse devant vole le survol — et `PQSCity.OnSphereReset` commence par **désactiver tous ses enfants** (`SetActive(false)`, `transformsActive = false`), tandis que Parallax rejoue son chargement de corps (`[Scatter Manager] body loading Kerbin`) au milieu de la manœuvre. Que ce soient des colliders de scatters posés sur la pelouse ou des bâtiments qui ne se réactivent pas tous, la géométrie autour de la R&D et de la station de suivi n'est pas celle de la piste et du SPH — d'où des bâtiments touchés et d'autres non.

**Correctif : le `[KSPScenario]` ne déclare plus que `GameScenes.FLIGHT`.** `ScenarioRunner` gate l'instanciation sur `targetScenes.Contains(HighLogic.LoadedScene)` (`ScenarioRunner.cs:321`) : pas de module, pas d'`OnLoad`, pas de reset. Ce n'est pas une optimisation de confort — hors du vol les decals ne décident de rien (ils ne servent qu'à filtrer les scatters du terrain sur lequel le joueur se tient), et on payait 3,5 s de chargement à chaque passage par le Centre spatial pour ça.

**Ce que la liste des scènes ne coûte PAS** — vérifié avant de la réduire, parce que l'inquiétude est légitime :

- **aucune perte de données en sauvant depuis le KSC.** `ScenarioRunner` ne reconstruit un `ProtoScenarioModule` que si son `moduleRef` est vivant (`ScenarioRunner.cs:416`) ; les autres recopient tels quels les `moduleValues` avec lesquels ils ont été chargés (`ProtoScenarioModule.Save`) ;
- **rien ne lit le scénario hors vol** : les deux seuls usages de `BulldozerScenario.Instance` sont dans `BulldozerCleanModule`, sur le chemin d'une passe.

⚠️ **Le piège est amorcé pour la prochaine fois**, et c'est pour ça que l'attribut porte un commentaire : le jour où on voudra lire les zones nettoyées depuis le KSC, le réflexe sera de rajouter `GameScenes.SPACECENTER` dans la liste — et le bug reviendra **avec**, silencieusement. La bonne réponse ce jour-là est de sortir l'injection d'`OnLoad`, pas de rallonger la liste.

Conséquence assumée : un decal fuité d'une autre partie n'est plus purgé qu'à la prochaine entrée en vol. Sans importance — la fuite ne se voit que sur du terrain qu'on parcourt.

⚠️ **La règle générale à retenir : ne jamais rebâtir un PQS dans une scène qui n'est pas celle du vol.** Le Centre spatial et la station de suivi sont posés sur le même PQS de Kerbin, avec des objets (`PQSCity`) qui ne s'attendent pas à voir leur sphère repartir de zéro sous eux.


### ✅ RÉSOLU le 2026-08-27 — ombres fantômes du surlignage : DEUX bugs, pas un

Ouvert le 2026-08-25, fermé le 2026-08-27. Le dossier a longtemps piétiné parce qu'on cherchait **une** cause : il y en avait deux, indépendantes, dont une seule produisait le symptôme qui se voyait.

#### Cause 1 (le symptôme) — un shader de KSP produit l'artefact, les autres non

Dessiner l'overlay avec **`KSP/Alpha/Unlit Transparent`** laisse des taches noires sur les rochers, qui survivent dans les frames suivantes en silhouettes **figées en espace écran**. Les quatre autres candidats de `ShaderCandidates`, essayés au même endroit dans la même minute, n'en montrent aucune.

C'est un fait de mesure, pas une théorie : le mécanisme interne du shader n'est pas connu (il n'expose pas `_ZWrite`, donc rien ne permet de vérifier l'hypothèse de l'écriture de profondeur depuis le C#). **Correctif : il est retiré de la liste, et le commentaire de `ShaderCandidates` interdit de l'y remettre.**

Deux leçons à garder :

- **`Particles/Alpha Blended` n'existe pas dans un KSP 1.12 stock.** `Shader.Find` rend `null` et on descend d'un cran **en silence** — pendant le diagnostic, demander ce candidat donnait en réalité `Legacy Shaders/Transparent/Diffuse`. D'où la ligne de log qui nomme le shader **réellement retenu** : sans elle, une bisection sur la liste est fausse.
- **Aucun de ces shaders n'expose `_ZWrite`.** Un `SetInt("_ZWrite", 0)` sur un shader qui code sa profondeur en dur ne fait rien, sans erreur. Ne pas croire protéger quoi que ce soit avec.

#### Cause 2 (le vrai bug de fond) — inflater un maillage de grappe le DÉPLACE

L'overlay était dessiné à l'échelle ×1,03, pour se poser *autour* du rocher plutôt que de se battre avec lui dans le z-buffer. Le réflexe est bon sur un caillou, faux sur une grappe :

> Une mise à l'échelle s'applique **autour du pivot du maillage**. Les grosses familles sont des grappes (`clusterhuge`) : un seul maillage dont le pivot est l'ancre, avec des blocs étalés sur 15 à 25 m autour. Le décalage vaut donc **`(facteur − 1) × distance au pivot`** — nul au centre, **60 cm à 20 m** pour 1,03.

Vérifié en jeu en faisant varier le facteur à chaud : l'écart est **radial** (chaque bloc poussé dans sa propre direction, en s'éloignant de l'ancre) et **croissant avec la distance au centre**. C'est la signature qui distingue ce bug de tout décalage global — une matrice périmée, une origine flottante ou un décalage de caméra translateraient **tout le voile du même vecteur**.

**⚠️ Le piège vaut au-delà du surlignage** : toute manipulation d'un maillage de scatter Parallax doit le **translater**, jamais le redimensionner, tant qu'on raisonne sur la position d'un bloc particulier.

#### Le correctif retenu pour le z-fighting

Supprimer l'inflation ne suffit pas : à la position exacte du rocher, le voile **clignote**. Parallax construit ses matrices d'instance sur le **GPU**, nous sur le **CPU** — les profondeurs coïncident au bruit flottant près, pas exactement.

D'où une **translation vers la caméra**, proportionnelle à la distance (`DepthBiasPerMetre = 0,002`, plancher 2 cm) : elle déplace la grappe **en bloc** sans toucher à sa forme, et reste constante à l'écran alors que la précision du z-buffer, elle, se dégrade avec la distance. Valeur calibrée au raisonnement, pas à la mesure : monter si du clignotement revient au loin, baisser si le voile flotte devant les rochers en vue rasante.

#### Ce que la bisection initiale avait raté, et pourquoi

Trois hypothèses avaient été testées et écartées (garde `LoadedSceneIsFlight`, `camera: null` → `Camera.main`, matériau `static` + `HideAndDontSave`). Les trois correctifs sont **conservés** — chacun se justifie indépendamment — mais aucun n'était la cause, et le temps perdu vient de deux erreurs de méthode :

- **Un essai de contrôle biaisé.** « Surlignage allumé mais rover en zone vide → pas d'ombres » avait servi à conclure que nos dessins étaient en cause. Sans rocher, il n'y a **rien** qui puisse produire un artefact : l'essai ne sépare pas « c'est notre dessin » de « il faut des rochers pour voir quoi que ce soit ». Le bon contrôle est **le même endroit, surlignage éteint**.
- **Le rechargement pris pour une condition.** Il n'en est pas une : **bouger la caméra suffit**. La repro coûtait 2 minutes au lieu de 2 secondes, ce qui a découragé toute bisection large. Chercher d'abord la repro la moins chère, avant de commencer à tester des hypothèses.

Ce qui a débloqué le dossier, ce sont deux observations précises du symptôme plutôt que des essais de correctif : **« c'est collé à l'écran »** (donc ni géométrie mal placée ni ombre, qui resteraient ancrées au sol) et **« le voile rouge est décalé du rocher »** (donc notre transformation, pas le pipeline). Et l'outillage qui les a exploitées : des **interrupteurs en vol** (shader, facteur d'inflation) qui ramènent un essai de 5 minutes de rebuild à une touche.

