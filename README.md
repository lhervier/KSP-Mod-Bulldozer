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

D'où le **découplage des deux rayons** (`ScatterCleaner.FootprintMargin` pour l'emprise, `BulldozerAction.radius` pour la zone nettoyée) :

| | Rôle | Valeur |
|---|---|---|
| `radius` du PQSMod | **emprise** : capture les quads + définit l'espace UV du masque | `halfDiag` **du quad** × `FootprintMargin` (1,1) — **~1090 m sur Kerbin**, ~610 m sur la Mun |
| disque dessiné dans le masque | **la zone réellement nettoyée** | ce que demande le joueur (20 m) |

La résolution du masque se déduit des deux : l'emprise (2×`radius`) étalée sur la texture. Elle n'est **pas** un réglage libre — voir « Taille du masque » ci-dessous.

### Le repère du masque : placer un point dans la texture

**L'espace couvert est un carré**, par construction : `u` et `v` sont calculés avec le *même* `radius` et étalés sur le *même* nombre de pixels, donc la texture couvre `2×radius` de côté. Elle doit donc être carrée elle aussi : une texture non carrée serait **étirée**, pas rognée.

⚠️ **La texture n'est PAS plaquée sur la forme du quad**, et un pixel n'est jamais un rectangle. Le masque est un carré du plan tangent, orienté par `rot`, qui **circonscrit** le quad sans s'aligner dessus — c'est précisément pourquoi il est dimensionné sur la **demi-diagonale**. Un pixel vaut donc `2×radius / MaskSize` mètres **dans les deux directions**, quelle que soit la forme du quad. `DrawDisk` le suppose déjà : son `radiusPx` est un scalaire unique, donc le disque y est tracé comme un cercle en espace pixel.

Conséquence à assumer : le quad étant inscrit **en diagonale** dans ce carré, environ **60 % des pixels tombent hors du quad**. Ce gaspillage ne vient pas de l'allongement des quads — un quad parfaitement carré en gaspille déjà 59 %, un quad 313×478 m en gaspille 62 %. Il est incompressible tant que le repère du masque est celui que Parallax impose.

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

### Taille du masque : un pixel = 5 m

**Décidé le 2026-08-24.** La texture était figée à 2048×2048 quel que soit le corps. Comme elle couvre l'emprise du quad, et que celle-ci varie d'un facteur ~50 entre Gilly et Eve, **la résolution réelle variait d'autant** — sans que rien ne le signale :

| Corps (`maxLevel` 9) | Arête du quad | m/pixel à 2048 | Disque de rayon 5 m |
|---|---|---|---|
| Gilly | 51 m | 0,039 | 258 px de diamètre |
| Mun | 781 m | 0,59 | 17 px |
| Kerbin | 1405 m | 1,07 | 9,4 px |
| Eve | 2734 m | **2,08** | **4,8 px** |

Deux défauts symétriques : Eve rendait le disque minimal sur 5 pixels, pendant que Gilly dépensait 8 Mo pour une précision de 4 cm dont personne ne profite.

**Règle retenue : la texture est dimensionnée pour qu'un pixel vaille toujours un carré de 5 m × 5 m au sol.**

```
côté couvert = 2 × footprint = 2 × halfDiag(quad) × FootprintMargin
MaskSize     = round(côté couvert / 5)     borné à [32, 2048]
```

| Corps | Arête du quad | Côté couvert | `MaskSize` | Mémoire |
|---|---|---|---|---|
| Gilly (9) | 51 m | 79 m | 32 (plancher) | 2 Ko |
| Mun à 45° | 313 × 478 m | 629 m | 126 | 32 Ko |
| Mun (9) | 781 m | 1215 m | 243 | 118 Ko |
| Kerbin (9) | 1405 m | 2186 m | 437 | 382 Ko |
| Eve (9) | 2734 m | 4253 m | 851 | 1,45 Mo |

Contre **8 Mo par quad partout** aujourd'hui. Les bornes ne sont que des garde-fous : aucun corps stock n'atteint le plafond (il faudrait une arête de quad > 6,5 km), et le plancher n'existe que pour éviter les textures dégénérées sur les très petits corps.

**Pas d'arrondi à la puissance de deux.** C'est la seule vraie source d'imprécision sur les 5 m : arrondir 851 → 1024 sur Eve donnerait des cases de **4,15 m** (17 % d'écart), différent sur chaque corps. Avec la taille exacte, l'écart tombe sous le millième (le simple arrondi à l'entier), et une `Texture2D` NPOT en bilinéaire + clamp ne pose aucun problème. Si les 5 m deviennent un **contrat de gameplay** (sélection par cases), c'est indispensable.

**La mémoire compte double.** `ScatterMask` retient le `byte[] _pixels` (pour les `DrawDisk` suivants) *et* la `Texture2D`. Les chiffres ci-dessus incluent les deux ; le commentaire du fichier qui annonce « 4 Mo par quad » ne comptait que la texture.

**La taille est dérivée, jamais persistée.** Elle se recalcule au chargement depuis le `halfDiag` du quad, puisque les masques sont reconstruits en rejouant les disques. La stocker exposerait à un changement de preset de détail (`maxLevel` change → l'arête change → la taille attendue change) — ce que le garde-fou `action.maxLevel` détecte et refuse déjà.

**La grille est propre à chaque quad.** Chaque masque a son origine et son orientation (`rot` dépend du centre du quad), donc les cases de deux quads voisins **ne sont pas alignées** : une zone à cheval peut faire un décrochement d'au plus une case (5 m) à la couture. C'est assumé — chercher à construire une grille globale se heurterait à la torsion mal conditionnée de `FromToRotation` près des pôles du corps, pour un gain nul sur du retrait de cailloux.

**Le rayon minimal suit la taille de case.** L'ancien seuil de 5 m était calibré sur ~0,7 m/pixel ; à 5 m par case il ne ferait plus que 2 cases de diamètre. Il est donc défini comme `3 × ScatterMask.MetersPerPixel`, soit **15 m**, et se recalera de lui-même si la taille de case bouge.

**Si la sélection passe un jour à des cases 5×5 :** stocker **le geste** (rectangle, disque), jamais les cases. Les cases sont la granularité de rasterisation dans le masque, pas une unité de persistance — une zone de 100×100 m fait déjà 400 cases, et c'est le même raisonnement qui avait fait rejeter le stockage des hauteurs par sommet. Bénéfice au passage : des gestes alignés sur la grille rendent le rejeu **bit à bit identique** et le recouvrement de deux zones exact.

### Ce que la multiplicité des masques ne coûte PAS

Un disque posé près d'un coin de quad doit être dessiné dans **4 masques** distincts. Deux craintes légitimes, toutes deux infondées — notées ici pour ne pas les ré-instruire :

- **Les 4 masques sont d'accord au demi-millimètre.** Chaque decal projette orthographiquement sur *son* plan tangent ; pour un point à distance angulaire `θ` du centre du decal, l'erreur vaut `R·θ³/6`, soit **0,45 mm** au coin d'un quad de Kerbin. Aucun décrochement visible ne viendra des repères — seulement, au pire, de la quantification en pixels (une case), aux 2 points où le bord du cercle croise une frontière de quad.
- **Le bord n'est pas un escalier.** `FilterMode.Bilinear` + seuil `< 0.5` dans le shader reconstruisent la frontière à la **demi-résolution du pixel** : un disque de 8 cases rend un octogone arrondi, pas un crénelage.

Et surtout : **le masque ne s'affiche jamais**. Il décide, rocher par rocher, lequel disparaît. Le bord perçu est celui des scatters survivants — déjà irrégulier à l'échelle de leur espacement. Une quantification à 5 m par-dessus une distribution pseudo-aléatoire à 2-3 m se lit comme une clairière aux bords naturels, ce qui est le rendu recherché.

**Troncature sur les petits corps (sans conséquence).** Sur Gilly, `FootprintMargin = 1.1` ne laisse que ~3,6 m de marge autour d'un quad de 51 m : un disque de 20 m posé près d'un coin **sort du masque**, et `DrawDisk` le clampe. C'est inoffensif — la partie tronquée est hors du quad, donc hors de la zone que ce decal contrôle, et le masque du quad voisin l'a dessinée en entier de son côté. La marge de 1,1 ne sert qu'à garantir que le carré contient le quad malgré la rotation, et pour ça elle suffit toujours.

### Architecture : un decal par quad

**Décidée le 2026-08-24, en remplacement du « un decal par zone ».** Le decal n'est plus posé sur le vaisseau mais **au centre du quad-feuille**, et il ne s'attache **qu'à ce quad-là**. La zone à nettoyer, elle, est dessinée dans son masque à la position réelle du vaisseau.

Ce que ça change :

- **La fusion devient un lookup.** Un quad a un decal ou il n'en a pas. Nettoyer une zone = pour chaque quad touché, compléter son masque s'il existe, en créer un sinon. Plus d'algorithme de fusion de zones.
- **Nos decals ne se gênent plus jamais**, quel que soit le nombre de chantiers ou leur proximité : la relation devient 1 pour 1.
- En contrepartie, **une zone à cheval crée plusieurs decals**, un par quad touché, chacun portant sa portion du disque. C'est mécanique, et c'est exactement le même code que la fusion — une seule fonction « dessiner un disque (lat/lon, rayon) dans le masque de ce quad ».

**Ce qui rend l'architecture possible** : `BulldozerScatterDecal` **override `OnQuadPreBuild`**. Sans ça, un decal de ~700 m de rayon capturerait tout un cône de 2,8 km (le ×4) et empêcherait les decals voisins de s'enregistrer — le problème serait juste déplacé. Notre override refuse tout quad qui n'est pas le sien :

```csharp
if (quad.subdivision != sphere.maxLevel) return;   // seuls les quads-feuilles portent des scatters
if (AngleTo(quad) > CenterTolerance) return;       // et seulement CELUI dont je suis le centre
```

Deux conséquences à ne pas perdre de vue :

- le decal étant **exactement** au centre de son quad, l'angle vaut zéro : le clamp avant `Acos` (§5, PIÈGE 1) n'est plus un correctif de confort, c'est **la condition de fonctionnement** ;
- on ne peut pas retenir la référence `PQ` (les quads sont détruits et recréés sans cesse) : un decal identifie son quad par la **position de son centre**, à quelques mètres près.

**Le test « ce quad est-il touché ? » est exact**, et sans repli.

Une arête droite sur la face du cube se projette en **arc de grand cercle** : les directions qui traversent un segment droit engendrent un plan, et ce plan passe par le centre du corps. Un quad projeté est donc exactement l'**intersection de quatre demi-espaces**. Les quatre coins se lisent sur `PQ.quadMatrix` (la matrice qui envoie le patch de référence sur la cellule de la face, coins en `(±0,5, 0, ±0,5)`), chaque arête donne une normale `n_k = C_k × C_k+1`, et le disque devient une calotte de rayon angulaire `α = rayon / R` :

```
le disque rate le quad  ⟺  ∃k : dot(centre, n_k) < −sin(α)
```

Quatre produits scalaires. **Aucune hypothèse de rectangle, aucun voisin requis**, valable quel que soit le biais du quad et jusqu'au bord de la zone subdivisée. Le test est **conservateur aux coins** — une calotte passée en diagonale au-delà d'un coin peut franchir les quatre plans sans toucher le quad : c'est le sens d'erreur inoffensif (un decal inutile), jamais l'inverse (un caillou oublié).

`TryGetEdgePlanes` vérifie au passage que les coins sont pris **dans l'ordre du tour**. Si une diagonale s'y glissait, les plans couperaient le quad au lieu de le borner ; les quatre normales seraient alors incohérentes (ni toutes rentrantes, ni toutes sortantes), et le quad est refusé avec une erreur plutôt que testé contre du n'importe quoi.

*Deux versions antérieures ont échoué ici, et les deux méritent d'être retenues.* La première prenait un rayon de recherche **calculé** (`NominalQuadEdge`) : elle ne trouvait **aucun** quad alors que le vaisseau était au coin de quatre. La seconde mesurait un repère tangent sur les voisins et testait une distance point-rectangle — juste au centre d'une face, mais elle cessait d'être conservative vers les coins du cube (un quad est un **parallélogramme**, pas un rectangle), et son repli « demi-diagonale + rayon », annoncé comme conservateur, reposait justement sur la formule qui **sous-estime de 27 %**. Il pouvait donc rater un quad : le bug d'origine, réintroduit dans le chemin censé s'en prémunir.

**Deux limites connues**, toutes deux hors géométrie :

- **Ce que Parallax n'a pas enregistré est invisible.** La recherche parcourt `ScatterComponent.scatterQuadData`, filtré à `subdivision == maxLevel` ; un quad de `maxLevel-1` portant des scatters serait écarté silencieusement. Sans effet tant que le rayon reste petit — le disque ne quitte pas le cœur de la zone de détail max, sous le vaisseau — mais à surveiller le jour où le rayon devient paramétrable.
- **« Au pire 4 quads » suppose un disque petit devant un quad.** Sur un petit corps les quads le sont aussi (`2R / 2^maxLevel` : 51 m sur Gilly à `maxLevel` 9, 25 m à 10), et un disque de 20 m peut en toucher davantage. Le code n'en dépend pas (la recherche renvoie une liste) — ne pas figer ce 4 ailleurs.

**Les quads impactés sont calculés au clic, pas au chargement.** Non pas parce que leur géométrie serait irrécupérable — elle se recalcule très bien depuis `quadMatrix` — mais parce que **le registre de Parallax n'existe qu'en vol** : lui seul dit quels quads peuvent porter des scatters, et il se peuple à la construction du PQS. On profite donc du clic pour relever le **centre de chaque quad touché** (la direction brute, jamais lat/lon) et la plus grande demi-diagonale, puis les stocker dans l'action. C'est stable d'un chargement à l'autre tant que le preset de détail ne change pas (`maxLevel` est donc stocké avec).

### PIÈGE — un quad n'a ni la taille qu'on croit, ni la forme qu'on croit

Trois niveaux de piège, les deux premiers déjà payés en jeu (Mun, `maxLevel = 9`, formule : 614 m) :

| Position | Arêtes réelles | Rapport à la formule |
|---|---|---|
| Pôle (centre d'une face) | **781 m** | ×1,27 — le maximum, soit 4/π |
| Équateur | **741 m** | ×1,21 |
| 45° (bord de face) | **313 × 478 m** | ×0,5 à ×0,78 — **et non carré** |

1. **La taille ne se calcule pas.** `(π/2 × R) / 2^maxLevel` suppose des quads uniformes **en angle** ; le PQS est un cube projeté, ils sont uniformes sur le **plan de la face**. *Conséquence vécue : la recherche de quads, calibrée sur la formule, n'en trouvait **aucun** alors que le vaisseau était au coin de quatre d'entre eux.*
2. **Un quad n'est pas carré.** Au bord d'une face, l'échelle vaut `1/(1+t²)` radialement mais `1/√(1+t²)` transversalement — un rapport de **1,41 à 45°**. Moyenner les deux axes ferait tester un carré que le quad n'a pas, avec le risque de **manquer** un quad réellement touché.
3. **Un quad n'est même pas un rectangle.** Les deux axes de la grille, perpendiculaires sur la face plate, ne le restent pas après projection : l'angle entre eux vaut 90° sur les médianes d'une face, ~101° à mi-face, et **120° à un coin du cube**. Un quad est un **parallélogramme**. Un test point-rectangle, même avec les deux côtés correctement mesurés, y cesse d'être conservatif.

**Où tester :** les 8 coins du cube sont à **lat ±35,26°** (soit `arctan(1/√2)`), **lon 45 / 135 / 225 / 315°**. C'est là que le maillage est le plus déformé — et non aux pôles, qui tombent au centre des faces `Yp`/`Yn` et sont au contraire l'endroit le **plus régulier** (tangent à la sphère, échelle 1, isotrope).

D'où le test exact décrit plus haut : les quatre coins viennent de `PQ.quadMatrix`, sans rien mesurer ni supposer de la forme. `QuadLocator.TryMeasureShape` mesure en plus les deux côtés **séparément**, et les deux demi-diagonales extrêmes (elles ne sont égales que sur un vrai rectangle : leur écart affiche le biais) — pour le diagnostic uniquement. La plus grande demi-diagonale **voyage avec chaque quad de l'action** (`halfDiag` du nœud `QUAD`) : au chargement il n'y a aucun quad à mesurer, et c'est elle qui dimensionne l'emprise de son decal. Sans elle le quad est **ignoré avec un avertissement** — il n'existe aucune formule de repli sûre, `NominalQuadEdge` étant précisément le piège n°1.

**Une valeur par quad, et non un maximum par action.** Un maximum resterait *correct* — sur-dimensionner une emprise ne casse rien : la résolution du masque est tenue à 5 m/pixel quelle que soit sa taille, `DrawDisk` projette avec le rayon que le shader relit, et l'appartenance d'un quad se joue sur la distance au centre dans `BulldozerScatterDecal.OnQuadPreBuild`, jamais sur le rayon. Il ne coûterait que de la mémoire de masque (∝ rayon²) sur les quads plus petits que le plus gros de l'action — négligeable entre voisins, mais pas si une zone enjambe une arête du cube (313 m contre 781 m sur la Mune). Mesurer par quad supprime la question, et rend `FindImpactedQuads` auto-descriptive : une liste de quads décrits chacun, sans paramètre de sortie agrégé.

### PIÈGE — les gros scatters sont des GRAPPES, pas des rochers

**Constaté en jeu sur la Mune (2026-08-25), après une enquête qui a d'abord accusé le mod à tort.**

Symptôme : un nettoyage à 20 m fait disparaître le gravier mais laisse des **blocs à 5 m du rover**. Recliquer n'y change rien. Tout le reste est pourtant parfait — 2 quads sélectionnés, masques posés, `readBlockMap=True` sur les 7 familles, aucune erreur.

**Cause : la granularité n'est pas le rocher, c'est la grappe.** Les familles `LargeRocks` / `HugeRocks*` de la Mune utilisent des modèles `clusterhuge` : **une instance = un groupe de blocs étalé sur des dizaines de mètres**. Or le block map supprime l'instance dont **l'ancrage** tombe dans le disque. Une grappe ancrée juste au-delà du rayon continue donc de projeter ses blocs vers l'intérieur.

> **zone réellement propre ≈ rayon demandé − étendue des grappes**

Mesuré sur la Mune : à 20 m un bloc survivait à 5 m ; à 40 m et à 60 m c'était propre. L'étendue est donc de l'ordre de **15 à 25 m**, et `ClearRadius = 40` laisse les ~20 m de propreté visée.

Ce que la lecture de la source Parallax a permis d'**écarter** au passage (à ne pas ré-instruire) :

- **le niveau de subdivision** — `ScatterEligible` rend un scatter *toujours* éligible sur un quad `maxLevel` ; les 7 familles sont bien sur le quad sous le rover ;
- **le kernel** — `GetDistributeKernel()` choisit le 3 pour `readBlockMap + simplexPerlin`, et les 7 familles de la Mune sont toutes en `simplexPerlin` : même kernel pour toutes ;
- **les colliders** — `OnColliderReadbackComplete` relit `outputScatterDataBuffer`, **le même buffer**, après filtrage : pas de chemin parallèle qui échapperait au masque ;
- **la granularité du test** — dans `TerrainScatters.compute` le block map est échantillonné **par instance** (`localCenterDir`, à l'intérieur de la boucle `_PopulationMultiplier`), pas par triangle.

**Et la contrainte structurelle qui interdit le contournement évident :** on ne peut PAS donner un rayon plus grand aux seules grosses familles. Le masque, son rayon, sa rotation et son centre viennent tous de `parent.blockMapPQSMod` — et Parallax n'accepte **qu'un decal par quad**. Donc **un masque, un rayon, pour les 7 familles**. `blockedScatters` choisit *quelles* familles sont bloquées, jamais *à quelle distance*.

**Décision : on documente, on n'impose rien.** Un rayon inférieur à l'étendue des grappes nettoie correctement les petits scatters et laisse les gros — c'est un arbitrage lisible pour le joueur, pas une erreur à refuser. `MinimumRadius` reste donc calé sur la seule résolution du masque. À garder en tête : ce chiffre est **calibré Mune**, et les autres corps embarquent des modèles de tailles très différentes (arbres sur Kerbin, végétation sur Laythe).

### Règles de refus

- **Rayon < 15 m** : refusé. Le seuil vaut `3 × ScatterMask.MetersPerPixel` — trois cases de masque, le point où le bilinéaire et le seuil 0,5 du shader reconstruisent encore un disque plutôt qu'une tache. Défini à partir de la taille de case, il se recale tout seul si elle change. **Volontairement pas relevé à l'étendue des grappes** : un petit rayon nettoie correctement les petits scatters, il laisse juste les gros debout.
- **Quad déjà possédé par un autre mod** : refusé, en nommant la zone. Vérifié **au clic** — `maskedScattersPerQuad` est déjà peuplé en vol — donc sans rechargement inutile. En pratique un seul cas existe dans tout `GameData` : **`ParallaxMaskKSC`** (Kerbin, rayon 1400 m, portée 5,6 km). Ni les autres sites de lancement, ni le mémorial Armstrong (qui a bien ses rochers) n'ont de decal d'exclusion.

### Contraintes

- **Zone circulaire**, rayon **40 m** (en dur pour l'instant, cf. « Ce qui reste à faire »), centrée sur le vaisseau. Le 40 vient des grappes de scatters, pas d'un choix esthétique — voir le PIÈGE ci-dessus.
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
| `Src/BulldozerCleanModule.cs` | `PartModule` : entrée PAW **« Clear scatters »**. Relève les quads impactés, applique les règles de refus, enregistre l'action puis save+reload. Posé sur tout module de commande par `GameData/BulldozerMod/BulldozerMod.cfg` (patch MM) — provisoire, la vraie pièce sera une lame |
| `Src/parallax/ParallaxInterop.cs` | Tout ce qu'on fait avec Parallax : pose du decal, liste des scatters d'un corps, purge du registre statique. **Compilé contre `ParallaxContinued.dll`** : une évolution de son API casse le build, pas le vol |
| `Src/parallax/BulldozerScatterDecal.cs` | Le decal lui-même : sous-classe du PQSMod de Parallax, **attachée à un seul quad** (celui dont elle occupe le centre) |
| `Src/terrain/QuadLocator.cs` | Géométrie des quads : arête, demi-diagonale, et **quels quads-feuilles un disque touche** (en vol uniquement) |
| `Src/terrain/ScatterMask.cs` | Le masque d'un decal (`Texture2D` 2048² en R8) et la projection lat/lon → pixel ; `DrawDisk` sert aussi bien à créer qu'à fusionner |
| `Src/terrain/ScatterCleaner.cs` | Purge nos decals, **regroupe les disques par quad** (un decal chacun), `ResetAndWait` des corps touchés |
| `Src/persistence/BulldozerAction.cs` | Une action `{ body, lat, lon, radius, maxLevel, QUAD*{ dir, halfDiag } }` |
| `Src/persistence/BulldozerScenario.cs` | `ScenarioModule` (`AddToAllGames`) : source de vérité par save ; son `OnLoad` déclenche l'injection |
| `Src/diagnostics/QuadOverlay.cs` | **Outil de test** : affichage en vol des quads voisins (MOD+B) — voir ci-dessous |
| `Src/diagnostics/BulldozerScatterDiagnostic.cs` | **Outil de test** : dump complet de la chaîne dans `KSP.log`, 8 s après l'entrée en vol |

### Se placer sur une limite de quad (`QuadOverlay`)

Ouvert avec **MOD+B** en vol (MOD = shift droit sous Windows). Il liste les **4 quads-feuilles les plus proches**, avec la distance du vaisseau à leur **centre** et leur propriétaire éventuel :

- deux distances égales ⇒ le vaisseau est sur **l'arête** entre ces deux quads ;
- quatre distances égales ⇒ il est sur le **coin** où quatre quads se rejoignent ;
- la ligne `spread` donne directement l'écart entre la plus petite et la plus grande — la faire tendre vers 0 revient à se placer au coin.

La dernière ligne annonce combien de quads un nettoyage toucherait à cet endroit : c'est le nombre de decals qui seraient créés, donc la valeur à surveiller pour tester un nettoyage à cheval.

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
> ✅ **Architecture « un decal par quad » validée le 2026-08-24** : nettoyages multiples dans une même partie, y compris à cheval sur quatre quads, chacun recevant son decal et sa portion de disque. La fusion (deux zones sur un même quad) passe par le même chemin.
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

### Limites observées en jeu

**Près du KSC, rien ne se nettoie.** Parallax y pose son propre decal, `ParallaxMaskKSC` (`radius = 1400` m, donc une portée de `atan(1400/600000) × 4` ≈ **5,6 km**), et un quad n'accepte qu'un seul decal : le nôtre est ignoré sur tous les quads déjà pris, silencieusement. Le log le nomme désormais explicitement (`Quad already owned by 'ParallaxMaskKSC'`). Sans conséquence pour l'usage visé — on ne construit pas de base sur le KSC — mais à savoir pour ne pas croire à une panne.

**Le nettoyage peut être partiel selon l'instant.** Un essai a laissé fleurs et herbes éparses ; le diagnostic montrait alors que le quad portant le vaisseau **n'était pas** pris en charge (le plus proche connu de Parallax était à 752 m, au-delà des 651 m possibles pour un quad de 920 m d'arête). Les scatters de ce quad avaient donc été générés sans notre masque. Rejoué au même endroit, le résultat était parfait. **Piste** : un quad ne s'enregistre qu'à sa construction (`isBuilt` n'est remis à `false` que dans `Awake()`), donc tout quad déjà bâti au moment de l'injection est perdu pour la passe. À surveiller si le cas se reproduit.

### ⚠️ EN COURS — ombres fantômes après surlignage + rechargement (cause INCONNUE)

**Ouvert le 2026-08-25, non résolu.** Ne pas repartir de zéro : voici ce qui est établi et ce qui est déjà écarté.

#### Symptôme

Des formes sombres restent au sol, à l'emplacement de rochers **bien réels**, décalées de leur objet — on les fait coïncider en bougeant la caméra.

#### Reproduction (stricte)

1. activer le surlignage (entrée PAW) ;
2. avoir **effectivement** des rochers surlignés à l'écran ;
3. recharger la partie ;
4. les ombres apparaissent, **sur exactement les rochers qui avaient été dessinés**.

Aucun nettoyage requis : ça se produit sur un terrain vierge, scénario sans la moindre action.

#### Établi par bisection en jeu

| Essai | Ombres ? |
|---|---|
| Surlignage **éteint** avant le rechargement | non |
| Surlignage allumé mais rover en zone vide — **aucun rocher dessiné** | non |
| Surlignage allumé **avec des rochers dessinés**, puis rechargement | **oui** |

⇒ ce sont **nos appels `Graphics.DrawMesh`**, et uniquement ceux réellement émis. Et elles n'apparaissent qu'**après** le rechargement.

Autres faits : éteindre le surlignage ne les efface pas (à reconfirmer, cf. plus bas) ; un second rechargement les efface ; laisser KSP en arrière-plan les efface aussi.

#### Écarté — ne pas refaire

| Hypothèse | Testée par | Verdict |
|---|---|---|
| Ce sont les ombres de Parallax (ses scatters sont dessinés en `ShadowCastingMode.On`) | essai « zone vide » | **faux** — sans dessin de notre part, rien |
| On dessine pendant la reconstruction de la scène (`Active` est `static`) | garde-fou `LoadedSceneIsFlight && FlightGlobals.ready` | **sans effet** |
| `camera: null` diffuse le dessin à toutes les caméras | passage à `Camera.main` | **sans effet** |
| Notre `Material` est détruit au changement de scène, laissant des entrées de rendu orphelines | `static` + `HideAndDontSave`, `OnDestroy` supprimé | **sans effet** |

Les trois correctifs sont **conservés** — chacun se justifie indépendamment — mais aucun n'est la cause.

#### Pistes pour la reprise

1. **Reconfirmer que l'extinction du surlignage ne les efface pas.** Toute l'analyse repose là-dessus et ça n'a pas été revérifié depuis plusieurs itérations. Si elles disparaissent, ce sont nos maillages rendus en sombre, et il faut chercher du côté du matériau **actif** (shader retenu, `renderQueue = 3100`, `_MainTex`) et non de son cycle de vie.
2. **Dessiner avec un matériau qu'on ne crée ni ne détruit** — celui de Parallax (`scatter.renderer.instancedMaterialLOD0`). Les rochers ne seront plus rouges, mais si l'artefact disparaît c'est notre matériau, s'il persiste c'est l'appel de dessin lui-même.
3. **Varier un paramètre à la fois** : `receiveShadows`, le layer (15), le `renderQueue`, l'inflation ×1,03, et le maillage (LOD0 de Parallax vs un cube maison). Le maillage maison est intéressant : on partage actuellement les `Mesh` de Parallax entre deux chemins de rendu.

#### Contournement en attendant

Un rechargement ou une mise en arrière-plan les efface. Pour rendre le bug inatteignable, forcer `ScatterHighlighter.Active = false` au démarrage de la scène (le joueur rallume après le rechargement) — **contournement, pas correctif**.

## 6. Ce qui reste à faire

- **Rayon paramétrable** côté joueur, puis la **pièce dédiée** (lame lourde, consommable après une maille) à la place du patch sur les modules de commande.
- **Retirer les outils de test** (`Src/diagnostics/`) : `BulldozerScatterDiagnostic` et `QuadOverlay` ne servent plus qu'en cas de régression.
- **Détail terrain : exigence caduque.** On voulait imposer le preset maximum, d'abord pour la finesse (elle vient de la texture, pas du maillage : sans objet), puis parce que la taille des quads en dépend. Les deux raisons sont tombées : l'arête est **mesurée en vol** et voyage dans l'action, et un changement de preset est détecté par la garde `maxLevel` (l'action est alors ignorée avec un avertissement, plutôt qu'appliquée de travers). Reste à décider ce qu'on fait de ces actions orphelines — les recalculer à la première occasion en vol serait plus élégant que de les abandonner.
- **Bords de zone** : coupure franche ou dégradé dans le masque (le sampler est bilinéaire, un masque adouci suffirait).

### Contraintes toujours valables

**Un seul decal par quad.** Le refus est explicite côté Parallax (« *Multiple MapDecalVertexRemoveScatter mods on the same quad! You must combine these decals into one* ») et **silencieux** : le second decal est ignoré sur les quads déjà pris. C'est précisément ce que l'architecture « un decal par quad » neutralise pour nos propres decals — mais la règle continue de s'appliquer face aux decals des **autres mods**, d'où le refus au clic (`ParallaxMaskKSC`).

**Le rayon du decal n'est pas le rayon nettoyé** (voir §2) : l'emprise doit rester calibrée sur la taille des quads.

### Restant ouvert
- **Trois familles d'objets au sol, une seule traitée.** Constaté en jeu :
  1. **scatters Parallax** — ceux qu'on nettoie ;
  2. **scatters stock** (`PQSLandControl`, exclus par `removeScatter` → `vbData.allowScatter`) — non traités. Parallax respectant ce flag, un `PQSMod_MapDecal` stock serait la voie complémentaire, sans objet tant que Parallax est installé ;
  3. **surface features de Breaking Ground** — ni des scatters ni du décor : des objets de gameplay persistants, scannables au bras robotique. Vu en plein milieu d'une zone nettoyée, à 7 m du rover. **À ne probablement PAS supprimer** (c'est de la science), même si elles portent un collider et peuvent gêner une grue. À rouvrir seulement si ça devient un problème concret.
- **Effets de bord visuels** : le decal déclare `MeshColorChannel`, d'où la question d'un impact éventuel sur la couleur du terrain et sur les biomes. Rien de visible aux essais.
