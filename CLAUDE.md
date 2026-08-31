# KSP-BulldozerMod — nettoyer une zone de ses rochers (scatters)

> **Ce fichier est LE document du projet** : `README.md` n'est qu'un pointeur vers lui. Un seul document, pour qu'il n'y en ait pas deux à garder en phase.
>
> **Objectif : supprimer les éléments décoratifs posés au sol (rochers Parallax) sur une bande devant le rover, de façon persistante, sans toucher au relief.**
>
> L'objectif d'origine — **niveler** le terrain — est **fermé**, pas reporté : §3 dit pourquoi, §7 garde ce qu'il en reste d'utile. Les connaissances KSP transverses (PQS, internals du jeu, conventions de dev) sont dans le [`CLAUDE.md` parent](../CLAUDE.md) de `kspmod\`.

## 1. Le besoin réel

Je construis mes bases planétaires avec cette technique (validée en jeu, elle marche) :

1. un **Clamp-O-Tron** posé au sol par un ingénieur ;
2. en **construction EVA**, des **poutrelles** assemblées pour former une structure qui **épouse la forme du sol** ;
3. des **ports de docking verticaux** sur cette structure ;
4. une **grue** qui vient docker les modules à la verticale dessus.

Pourquoi cette technique plutôt que « poser des vaisseaux et les docker entre eux » : cette dernière ne survit pas au rechargement. KSP **ne mémorise pas les forces appliquées entre les parties** ; il repositionne la base sans contraintes, une pièce se retrouve dans le sol, la physique démarre, la base est propulsée vers le haut et tout explose. Avec une structure de poutrelles qui suit le relief, la base est déjà à sa place quand la physique reprend → pas d'explosion.

**Le problème aujourd'hui : Parallax.** Le rendu est superbe et je veux le garder, mais les rochers qu'il sème au sol rendent la **construction impossible** : la grue ne peut plus aller où elle veut autour de la base.

D'où le besoin : **pouvoir nettoyer une zone (potentiellement grande) de ses rochers**, une fois, au moment de choisir l'emplacement d'une base.

## 2. Objectif et mécanisme

**Supprimer les scatters sur une passe de lame devant le rover, de façon persistante, sans déformer le relief.**

### Le levier : le PQSMod d'exclusion de Parallax

Parallax n'utilise **pas** le `removeScatter` du stock pour ses propres scatters : il embarque son PQSMod, `PQSMod_MapDecalVertexRemoveScatter` ([source](file:///d:/kspmod-ext/Parallax-Continued/Mod%20Source/Parallax/PQS%20Mods/MapDecalVertexRemoveScatter.cs)). Ses propriétés, **lues dans le source puis confirmées en jeu** (voir §5) :

- **il ne touche jamais la hauteur** : pas de `OnVertexBuildHeight`, il ne déclare que `requirements = MeshColorChannel`. L'exclusion sans déformation est **native**, pas un détournement ;
- il croise **deux axes** : le **quoi** — une liste `blockedScatters` de **types** de scatters (jamais des instances) — et le **où** — un **masque `colorMap` (`Texture2D`)** projeté sur l'emprise du decal ;
- le masque est échantillonné **dans le compute shader** : canal **`.r`**, seuil **`< 0.5` = bloqué**, `linearClampSampler` ([`TerrainScatters.compute:176`](file:///d:/kspmod-ext/Parallax-Continued/Assets/Shaders/Scatters/TerrainScatters.compute)).

Deux conséquences déterminantes :

- **La forme de la zone est libre.** `radius` ne définit qu'une **emprise maximale** ; à l'intérieur, c'est la texture qui décide, pixel par pixel. Cercle, carré, forme quelconque — au choix.
- **La finesse ne dépend pas du maillage PQS.** Le shader tourne **un thread par triangle**, tire un point candidat et échantillonne le masque **à la position de ce point** : la résolution effective est celle de la **texture**. Le grain de ~63 m entre sommets, qui condamnait l'objectif de nivellement (§3), **ne s'applique donc pas au nettoyage** : une zone de 20 m se rend au mètre près (mesurée à 19,9 m en jeu, §5).

### Ce qu'on bloque

**Tous les types de scatters du corps.** Le but est une zone **visuellement nettoyée**, pas seulement praticable. On ne filtre donc pas sur le `collisionLevel` déclaré en config (0 = aucun collider → 4 ; sur la Mun : `TinyRocks`=0 … `HugeRocks`=4). Ce champ reste bon à connaître, mais ne sert pas de critère.

### Le cœur du mod : un decal par quad, et on n'écrit que des pixels

**Un decal est posé au centre d'un quad-feuille, et sa texture couvre ce quad — tout ce quad, et à peu près rien de plus.** Un quad a notre decal ou il ne l'a pas.

**Nettoyer une zone, c'est écrire des pixels dans ces textures. Rien d'autre.** On ne pose pas un decal par passe, ni un decal par chantier, ni un decal « de zone » : le nombre de decals d'une partie ne dépend que du **nombre de quads touchés**, jamais du nombre de passes. Une seconde passe sur un quad déjà équipé se contente de noircir des pixels de plus dans le masque existant — la fusion de deux zones est donc un lookup, pas un algorithme.

**Pourquoi au centre du quad, et pas sous le rover.** Parce que ce n'est pas nous qui décidons quels quads un decal gouverne, c'est Parallax, en comparant la position du decal au **centre du quad** (`quad.positionPlanetRelative`, `PQ.cs:388`) :

```csharp
quadAngle = Acos(Dot(quad.positionPlanetRelative, normalisedPosition));
if (quadAngle < inclusionAngle) { maskedScattersPerQuad.Add(quad, this); }   // inclusionAngle = atan(radius/sphere.radius) * 4
```

Le `radius` du decal est donc **une portée de capture de quads**, pas une taille de zone. Lui donner la taille de ce qu'on veut nettoyer (20 m) ne capturerait **aucun quad** — `inclusionAngle` vaudrait ~80 m au sol quand un quad-feuille fait ~900 m d'arête, donc le centre du quad qui nous porte serait presque toujours hors de portée. Et ça échouerait **en silence** : aucun quad attaché, aucun effet, aucune erreur dans le log.

L'emprise est donc dictée par le **quad**, jamais par la zone : `halfDiag` du quad × `FootprintMargin` (1,1), soit ~1090 m sur Kerbin et ~610 m sur la Mune. Et si l'emprise a la taille d'un quad, le seul endroit qui la place bien est le **centre de ce quad** — c'est là que le carré du masque contient son quad avec le moins de gâchis, et c'est ce qui rend la relation decal↔quad univoque.

Deux conséquences à ne jamais perdre de vue :

- **la résolution du masque se déduit de l'emprise** (2 × `radius` étalés sur la texture), ce n'est pas un réglage libre — voir « Taille du masque » ;
- **un seul decal par quad**, et c'est Parallax qui l'impose. Le refus est explicite dans son code (« *Multiple MapDecalVertexRemoveScatter mods on the same quad! You must combine these decals into one* ») et **silencieux à l'exécution** : le second decal est simplement ignoré sur les quads déjà pris. L'architecture « un par quad » neutralise le problème entre **nos** decals, par construction. Elle ne peut rien contre ceux des **autres mods** : d'où le refus au clic quand un quad est déjà possédé (en pratique `ParallaxMaskKSC`, cf. « Règles de refus »).

### Le repère du masque : placer un point dans la texture

**L'espace couvert est un carré**, par construction : `u` et `v` sont calculés avec le *même* `radius` et étalés sur le *même* nombre de pixels, donc la texture couvre `2×radius` de côté. Elle doit donc être carrée elle aussi : une texture non carrée serait **étirée**, pas rognée.

⚠️ **La texture n'est PAS plaquée sur la forme du quad**, et un pixel n'est jamais un rectangle. Le masque est un carré du plan tangent, orienté par `rot`, qui **circonscrit** le quad sans s'aligner dessus — c'est précisément pourquoi il est dimensionné sur la **demi-diagonale**. Un pixel vaut donc `2×radius / MaskSize` mètres **dans les deux directions**, quelle que soit la forme du quad. `BandRaster` le suppose déjà : sa largeur de bande est un scalaire unique en pixels, valable dans toutes les directions.

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

C'est déterminant : une **bande a une direction**, et une orientation devinée la ferait pointer ailleurs. On ne devine donc pas — on rejoue la même transformation en C# (`rot` est public), via `BulldozerScatterDecal.RotationFor`, **seul** endroit où cette expression existe. `ParallaxInterop.DecalAngle` fige le `angle` de nos decals à 0 pour que la prédiction et le decal réel ne puissent pas diverger.

```csharp
Vector3d dir = body.GetRelSurfaceNVector(lat, lon);   // le point à nettoyer
Vector3 v = decal.rot * (Vector3)dir;                 // le rot du decal, celui-là même qu'utilise le shader
double u = (v.x * body.Radius / decal.radius + 1.0) * 0.5;
double w = (v.z * body.Radius / decal.radius + 1.0) * 0.5;
// -> pixel (u * size, w * size), au flip vertical près selon la convention de SetPixels32 (à vérifier à l'implémentation)
```

### Taille du masque : un pixel = 5 m

**La texture est dimensionnée pour qu'un pixel vaille toujours un carré de 5 m × 5 m au sol.**

```
côté couvert = 2 × footprint = 2 × halfDiag(quad) × FootprintMargin
MaskSize     = round(côté couvert / 5)     borné à [32, 2048]
```

**Une taille de texture fixe ne peut pas marcher**, et la raison est structurelle : le masque couvre l'emprise du quad, or **cette emprise varie d'un facteur ~7 d'un corps à l'autre** (mesuré aux deux extrêmes du stock : 299 m sur Gilly, 2016 m sur Eve — soit 3,5 Ko de masque contre 158 Ko). Une texture unique ferait donc varier la résolution au sol d'autant. Dériver la taille de l'emprise garde la case constante **et** la mémoire proportionnée.

⚠️ **`maxLevel` n'est PAS le même sur tous les corps — il compense en partie leur rayon.** Mesuré en jeu au preset de détail **maximum** : Gilly **7**, Mune **9**, Kerbin **10**, Eve **10**, de sorte qu'un quad fait toujours entre ~190 m et ~1320 m d'arête malgré un rapport de rayons de 1 à 54. Tout raisonnement bâti sur « `maxLevel` = 9 partout » — ce que ce document affirmait avant le 2026-08-27 — surestime donc massivement les petits corps.

| Corps | `maxLevel` | Arête du quad | Côté couvert | `MaskSize` | m/px | Mémoire |
|---|---|---|---|---|---|---|
| **Gilly** | 7 | 189 × 196 m | 299 m | 60 | 4,98 | 3,5 Ko |
| **Mune** | 9 | 728 × 754 m | 1153 m | 231 | 4,99 | 52 Ko |
| Mune à 45° | 9 | 313 × 478 m | 629 m | 126 | 4,99 | 15 Ko |
| **Kerbin** | 10 | 1091 × 1131 m | 1728 m | 346 | 5,00 | 117 Ko |
| **Eve** | 10 | 1271 × 1318 m | 2016 m | 403 | 5,00 | 158 Ko |

Les corps en gras sont **mesurés en jeu** (dumps du 2026-08-27, terrain detail max) ; « Mune à 45° » extrapole une mesure d'arête au bord d'une face de cube. **Les deux extrêmes de l'échelle stock sont donc tenus** — Gilly le plus petit corps, Eve le plus gros corps rocheux — et Eve plafonne à 403 px, moitié moins que les 851 px longtemps annoncés ici.

**Aucune des deux bornes n'est atteinte en stock, et c'est mesuré aux deux bouts.** Le plafond demanderait une arête de quad > 6,5 km, quand le plus gros corps rocheux — Eve — en mesure 1,3 km (403 px pour un plafond à 2048). Le plancher demanderait une arête < ~103 m, quand le plus petit — Gilly — en mesure 190. Les deux bornes ne servent donc qu'aux corps ajoutés (Kopernicus). Le contrat « 1 px = 5 m » tient sur toute l'échelle stock.

**Pas d'arrondi à la puissance de deux.** C'est la seule vraie source d'imprécision sur les 5 m : arrondir 346 → 512 sur Kerbin donnerait des cases de **3,4 m** (32 % d'écart) quand 60 → 64 sur Gilly donnerait 4,7 m (6 %) — un décalage différent sur chaque corps. Avec la taille exacte, l'écart tombe sous le millième (le simple arrondi à l'entier), et une `Texture2D` NPOT en bilinéaire + clamp ne pose aucun problème. Et les 5 m **sont** un contrat de gameplay : les deux curseurs avancent d'une case à la fois, et le quota des lames se compte en cases (§6). L'arrondi exact n'est donc pas un luxe.

**Un seul octet par pixel, et une seule copie.** `ScatterMask` ne garde **pas** de `byte[]` à côté de sa texture : il écrit dans les pixels de la `Texture2D` elle-même, via `GetRawTextureData<byte>()` — qui n'est pas une copie mais une **vue** (`NativeArray<byte>` construit par `ConvertExistingDataToNativeArray(GetWritableImageData(0), …, Allocator.None)`). Les chiffres ci-dessus sont donc `MaskSize²` octets, un point c'est tout. Deux conséquences à ne pas casser : la texture doit rester **lisible** (`Apply(false, false)`, jamais `makeNoLongerReadable: true`, sinon `GetRawTextureData` lève), et la vue se **relit à chaque usage** plutôt que se mettre en cache, sa validité n'étant garantie que jusqu'à la prochaine modification de la texture.

**La taille est dérivée, jamais persistée.** Elle se recalcule au chargement depuis le `halfDiag` du quad, puisque les masques sont reconstruits en rejouant les passes. La stocker exposerait à un changement de preset de détail (`maxLevel` change → l'arête change → la taille attendue change) — ce que le garde-fou `action.maxLevel` détecte et refuse déjà.

**La grille est propre à chaque quad.** Chaque masque a son origine et son orientation (`rot` dépend du centre du quad), donc les cases de deux quads voisins **ne sont pas alignées** : une zone à cheval peut faire un décrochement d'au plus une case (5 m) à la couture. C'est assumé — chercher à construire une grille globale se heurterait à la torsion mal conditionnée de `FromToRotation` près des pôles du corps, pour un gain nul sur du retrait de cailloux.

**On stocke le geste, jamais les cases.** Les cases sont la granularité de rasterisation dans le masque, pas une unité de persistance — une passe de 200 × 10 m fait déjà 80 cases, et c'est le même raisonnement qui avait fait rejeter le stockage des hauteurs par sommet. Une action porte donc `{ origine, direction, longueur, largeur }`. La largeur y **voyage** parce qu'elle est un réglage joueur : rejouer une vieille action doit utiliser la largeur avec laquelle elle a été demandée, pas celle du curseur au moment du chargement.

### La passe de lame — et pourquoi la pixelisation devient le sujet

Le geste est un **rectangle** partant du **centre du vaisseau vers l'avant**, **longueur de 5 à 200 m** et **largeur de 5 à 20 m**, les deux au pas de 5 m (= une case de masque) et réglables dans le PAW. Défaut : 50 × 10 m. La direction est le **cap du navball** — l'axe `+up` du point de contrôle (`NavBall` cible `ActiveVessel.ReferenceTransform`) projeté sur l'horizontale — donc « control from here » vise la passe comme il pilote le rover. Si cet axe est plus vertical qu'horizontal, la passe est **refusée** : sa projection horizontale ne veut plus rien dire (voir « Règles de refus » ci-dessous — c'est un piège qui a coûté cher).

⚠️ **5 m de large = UNE case de masque. C'est la limite de résolution, et ça change tout.**

D'abord, « 1 pixel » n'existe pas comme unité stable : chaque quad a son masque, avec son origine **et son orientation** (`rot` dérive de `FromToRotation(centre du quad, up)`, arbitraire). La grille n'est alignée ni sur le nord, ni sur le cap du rover, ni sur celle du quad voisin. « 5 m » est une longueur, pas un pixel.

Ensuite, la règle de tracé cesse d'être un détail :

| Règle | Résultat à cette largeur |
|---|---|
| centre de case dans le rectangle (la règle naturelle) | bande **perforée** : à 45° les cases effacées ne se touchent qu'aux coins, où le bilinéaire vaut pile `0.5` — que le `< 0.5` **strict** du shader ne retient pas |
| couverture (anti-aliasing) | **pire** : une bande à cheval sur deux rangées donne deux rangées à ~127, jamais sous le seuil → **rien** n'est nettoyé |
| **toute case touchée par le rectangle** ✅ retenue | bande continue à tout angle |

**Mesuré** en rejouant les règles de `BandRaster` hors du jeu, sur tous les angles et tous les décalages sous-case :

- couloir nettoyé **jamais pincé** : au pire **1,08 case (5,4 m)** de large, donc un peu plus que la lame ;
- mais il **s'élargit à 3,16 cases (15,8 m)** vers 45° ;
- et il peut se décaler d'une **demi-case** de l'axe de la lame → jusqu'à **12 % de la trace de la lame reste debout**, en liseré sur un bord.

Ce dernier point est exactement pourquoi le **highlight échantillonne le masque** et non le rectangle (`ScatterHighlighter`, cf. §4) : aucune approximation géométrique ne le montrerait. Combiné aux **grappes** (§ PIÈGE ci-dessous), qui laissent debout des blocs ancrés jusqu'à 25 m à côté, la conséquence assumée est qu'une passe étroite ne dégage pas un couloir propre au premier essai — le joueur le **voit** avant de valider, et repositionne.

### Ce que la multiplicité des masques ne coûte PAS

Une passe posée près d'un coin de quad doit être dessinée dans **4 masques** distincts. Deux craintes légitimes, toutes deux infondées — notées ici pour ne pas les ré-instruire :

- **Les 4 masques sont d'accord au demi-millimètre.** Chaque decal projette orthographiquement sur *son* plan tangent ; pour un point à distance angulaire `θ` du centre du decal, l'erreur vaut `R·θ³/6`, soit **0,45 mm** au coin d'un quad de Kerbin. Aucun décrochement visible ne viendra des repères — seulement, au pire, de la quantification en pixels (une case), là où le bord de la zone croise une frontière de quad.
- **Le bord n'est pas un escalier.** `FilterMode.Bilinear` + seuil `< 0.5` dans le shader reconstruisent la frontière à la **demi-résolution du pixel**, donc au milieu d'une case et non sur sa grille.

Et surtout : **le masque ne s'affiche jamais**. Il décide, rocher par rocher, lequel disparaît. Le bord perçu est celui des scatters survivants — déjà irrégulier à l'échelle de leur espacement. Une quantification à 5 m par-dessus une distribution pseudo-aléatoire à 2-3 m se lit comme une clairière aux bords naturels, ce qui est le rendu recherché.

**Troncature sur les petits corps (sans conséquence, et vérifiée).** Sur Gilly, `FootprintMargin = 1.1` ne laisse que ~13 m de marge autour d'un quad de 190 m : une passe de 200 m **sort du masque**, systématiquement puisqu'elle est plus longue qu'un quad, et `DrawBand` la clampe. Constaté en jeu le 2026-08-27 (§5) : `band px start=(-10.2,35.5) end=(26.7,19.5)` sur un masque de 60 px, la bande sortant des deux côtés. C'est inoffensif — la partie tronquée est hors du quad, donc hors de la zone que ce decal contrôle, et le masque du quad voisin l'a dessinée en entier de son côté. La marge de 1,1 ne sert qu'à garantir que le carré contient le quad malgré la rotation, et pour ça elle suffit toujours.

### Comment un decal se limite à son quad

Le prix de l'architecture est mécanique : **une passe à cheval crée plusieurs decals**, un par quad touché, chacun portant sa portion de la bande. C'est le même code que la fusion — une seule fonction « dessiner la bande dans le masque de ce quad ».

**Ce qui rend l'architecture possible** : `BulldozerScatterDecal` **override `OnQuadPreBuild`**. Sans ça, un decal de ~700 m de rayon capturerait tout un cône de 2,8 km (le ×4) et empêcherait les decals voisins de s'enregistrer — le problème serait juste déplacé. Notre override refuse tout quad qui n'est pas le sien :

```csharp
if (quad.subdivision != sphere.maxLevel) return;   // seuls les quads-feuilles portent des scatters
if (AngleTo(quad) > CenterTolerance) return;       // et seulement CELUI dont je suis le centre
```

Deux conséquences à ne pas perdre de vue :

- le decal étant **exactement** au centre de son quad, l'angle vaut zéro : le clamp avant `Acos` (§5, PIÈGE 1) n'est pas un correctif de confort, c'est **la condition de fonctionnement** ;
- on ne peut pas retenir la référence `PQ` (les quads sont détruits et recréés sans cesse) : un decal identifie son quad par la **position de son centre**, à quelques mètres près.

**Le test « ce quad est-il touché ? » est exact**, et sans repli.

Une arête droite sur la face du cube se projette en **arc de grand cercle** : les directions qui traversent un segment droit engendrent un plan, et ce plan passe par le centre du corps. Un quad projeté est donc exactement l'**intersection de quatre demi-espaces**. Les quatre coins se lisent sur `PQ.quadMatrix` (la matrice qui envoie le patch de référence sur la cellule de la face, coins en `(±0,5, 0, ±0,5)`), chaque arête donne une normale `n_k = C_k × C_k+1`, et la bande est pré-sélectionnée par une **calotte qui la contient entièrement** (centre = milieu de la bande, rayon angulaire `α = SurfaceBand.CoarseRadius / R`) :

```
la calotte rate le quad  ⟺  ∃k : dot(centre, n_k) < −sin(α)
```

Cette pré-sélection est **volontairement généreuse** : le verdict exact vient ensuite, et c'est le seul qui décide — « au moins une case du masque de ce quad est-elle effacée ? » (`BandRaster.ClearsAnyCell`). Un quad que la bande ne fait que longer ne reçoit donc pas de decal. Faire l'intersection bande/quad sphérique ici aurait dupliqué, sous une autre forme, ce même test.

Quatre produits scalaires. **Aucune hypothèse de rectangle, aucun voisin requis**, valable quel que soit le biais du quad et jusqu'au bord de la zone subdivisée. Le test est **conservateur aux coins** — une calotte passée en diagonale au-delà d'un coin peut franchir les quatre plans sans toucher le quad : c'est le sens d'erreur inoffensif (un decal inutile), jamais l'inverse (un caillou oublié).

`TryGetEdgePlanes` vérifie au passage que les coins sont pris **dans l'ordre du tour**. Si une diagonale s'y glissait, les plans couperaient le quad au lieu de le borner ; les quatre normales seraient alors incohérentes (ni toutes rentrantes, ni toutes sortantes), et le quad est refusé avec une erreur plutôt que testé contre du n'importe quoi.

*Deux versions antérieures ont échoué ici, et les deux méritent d'être retenues.* La première prenait un rayon de recherche **calculé** (`NominalQuadEdge`) : elle ne trouvait **aucun** quad alors que le vaisseau était au coin de quatre. La seconde mesurait un repère tangent sur les voisins et testait une distance point-rectangle — juste au centre d'une face, mais elle cessait d'être conservative vers les coins du cube (un quad est un **parallélogramme**, pas un rectangle), et son repli « demi-diagonale + rayon », annoncé comme conservateur, reposait justement sur la formule qui **sous-estime de 27 %**. Il pouvait donc rater un quad : le bug d'origine, réintroduit dans le chemin censé s'en prémunir.

**Deux limites connues**, toutes deux hors géométrie :

- **Ce que Parallax n'a pas enregistré est invisible.** La recherche parcourt `ScatterComponent.scatterQuadData`, filtré à `subdivision == maxLevel` ; un quad de `maxLevel-1` portant des scatters est écarté **silencieusement**. ⚠️ **Ce n'est pas théorique** : à 200 m on peut sortir du cœur de subdivision max entretenu autour du vaisseau, et le bout de la bande serait alors ignoré sans un mot. **À vérifier en jeu** (une passe longue dont l'extrémité reste sale alors que le highlight ne montrait rien là-bas est le symptôme).
- **Ne figer aucun nombre de quads.** Une passe de 200 m en traverse plusieurs par construction : 4 sur la Mune, 3 sur Gilly (mesuré ; ses quads font 190 m, mais `maxLevel` y vaut 7). Le code n'en dépend pas — la recherche renvoie une liste.

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

**Cause : la granularité n'est pas le rocher, c'est la grappe.** Les familles `LargeRocks` / `HugeRocks*` de la Mune utilisent des modèles `clusterhuge` : **une instance = un groupe de blocs étalé sur des dizaines de mètres**. Or le block map supprime l'instance dont **l'ancrage** tombe dans la zone nettoyée. Une grappe ancrée juste à côté continue donc de projeter ses blocs vers l'intérieur.

> **zone réellement propre ≈ rayon demandé − étendue des grappes**

Mesuré sur la Mune : à 20 m un bloc survivait à 5 m ; à 40 m et à 60 m c'était propre. L'étendue est donc de l'ordre de **15 à 25 m** — bien plus large que la lame. La conséquence est assumée et déplacée sur le joueur : une passe de 5 m ne fait pas tomber les grappes, une de 20 m en fait tomber bien davantage, et le **highlight** est ce qui le lui montre avant de valider.

Ce que la lecture de la source Parallax a permis d'**écarter** au passage (à ne pas ré-instruire) :

- **le niveau de subdivision** — `ScatterEligible` rend un scatter *toujours* éligible sur un quad `maxLevel` ; les 7 familles sont bien sur le quad sous le rover ;
- **le kernel** — `GetDistributeKernel()` choisit le 3 pour `readBlockMap + simplexPerlin`, et les 7 familles de la Mune sont toutes en `simplexPerlin` : même kernel pour toutes ;
- **les colliders** — `OnColliderReadbackComplete` relit `outputScatterDataBuffer`, **le même buffer**, après filtrage : pas de chemin parallèle qui échapperait au masque ;
- **la granularité du test** — dans `TerrainScatters.compute` le block map est échantillonné **par instance** (`localCenterDir`, à l'intérieur de la boucle `_PopulationMultiplier`), pas par triangle.

**Et la contrainte structurelle qui interdit le contournement évident :** on ne peut PAS donner un rayon plus grand aux seules grosses familles. Le masque, son rayon, sa rotation et son centre viennent tous de `parent.blockMapPQSMod` — et Parallax n'accepte **qu'un decal par quad**. Donc **un masque, un rayon, pour les 7 familles**. `blockedScatters` choisit *quelles* familles sont bloquées, jamais *à quelle distance*.

**Décision : on documente, on n'impose rien.** Un rayon inférieur à l'étendue des grappes nettoie correctement les petits scatters et laisse les gros — c'est un arbitrage lisible pour le joueur, pas une erreur à refuser. `MinimumRadius` reste donc calé sur la seule résolution du masque. À garder en tête : ce chiffre est **calibré Mune**, et les autres corps embarquent des modèles de tailles très différentes (arbres sur Kerbin, végétation sur Laythe).

### Règles de refus

- **Cap inutilisable** : refusé. L'axe du point de contrôle doit être **plus horizontal que vertical** (`|horizontale| > |verticale|`, soit à moins de 45° de l'horizon).

  ⚠️ **Un petit seuil ne marche pas, c'est mesuré en jeu (2026-08-27).** Sur une pente, un axe de contrôle qui vise le **ciel** garde une composante horizontale importante — la direction dans laquelle le pont du rover penche, qui n'est pas un cap. Avec un seuil à `sin 10° = 0,17`, les points de contrôle « Up » **et** « Reversed » d'une Conserve Mk2 (`orientation = 0,0,180`, donc nez vers le bas) passaient tous les deux et envoyaient une passe de 200 m dans une direction arbitraire — les deux **exactement à 180°** l'une de l'autre, signature du fait qu'elles lisaient le même dévers. Le symptôme pour le joueur : « Up » ne surligne rien de visible (la bande part hors champ), « Reversed » surligne un paquet de cailloux qui n'a aucun rapport avec l'avant du rover.

  Rappel de repère (vérifié dans `ModuleCommand.cs:205`, où KSP dessine sa flèche « forward ») : le nez d'un point de contrôle est `controlPoint.transform.up`.
- **Lame trop haute** : refusée au-delà de **0,50 m** entre le **tranchant** et le sol. Depuis que les lames collisionnent (§6), une lame qui touche le sol bloque le rover : le joueur la porte donc sur un **axe robotisé** et la descend pour travailler. Ce seuil est ce qui rend ce geste obligatoire — sans lui, une lame boulonnée en haut de la caisse nettoierait aussi bien.
- **Lame mal alignée** : refusée au-delà de **15°** entre l'axe **haut** de la lame et la **normale du sol**. Un seul angle, et il couvre le tangage **et** le roulis : un axe contre une normale dit tout de la façon dont la lame est posée.

  ⚠️ **Mesuré contre le SOL, pas contre l'horizon**, et c'est structurant : sur une pente, une lame correctement posée suit la pente. Un test « à l'horizontale » refuserait exactement le rover qui fait ce qu'il faut. C'est une question **différente** de celle du cap ci-dessus, qui elle reste posée contre le géoïde et garde son seuil large de 45° pour cette raison même.

  La mesure est un **raycast** vers le bas depuis le tranchant, sur le layer **15 (`Local Scenery`)** — celui que KSP lui-même interroge pour `Vessel.heightFromTerrain`. Il part **5 m au-dessus** du tranchant : une lame qui travaille est *enfoncée* dans le sol, et un rayon parti du tranchant même commencerait à l'intérieur du collider de terrain et ne toucherait rien. Le hit donne d'un coup la hauteur (`distance − 5`, négative quand la lame est enterrée) **et** la normale du sol.

  Le point de mesure est le **coin du modèle à la fois le plus bas et le plus en avant** — c'est le tranchant, le modèle étant écrit +Y en haut et +Z vers l'avant. Il est **déduit des maillages** au premier usage, jamais déclaré dans le `.cfg` : il suit ainsi les trois largeurs, et un futur `EDGE_DROP`, sans seconde description de la forme à garder en phase.
- **Pas de seuil de taille** : les deux curseurs sont bornés, donc toute passe est légale par construction — minimum une case (5 m) dans chaque direction. **Volontairement pas de minimum calé sur l'étendue des grappes** : une passe étroite nettoie correctement les petits scatters, elle laisse juste les gros debout, et c'est au joueur d'arbitrer (le highlight le lui montre avant de valider).
- **Quad déjà possédé par un autre mod** : refusé, en nommant la zone. Vérifié **au clic** — `maskedScattersPerQuad` est déjà peuplé en vol — donc sans rechargement inutile. En pratique un seul cas existe dans tout `GameData` : **`ParallaxMaskKSC`** (Kerbin, rayon 1400 m, portée 5,6 km). Ni les autres sites de lancement, ni le mémorial Armstrong (qui a bien ses rochers) n'ont de decal d'exclusion.

### Contraintes

- **Bande rectangulaire** partant du centre du vaisseau vers l'avant, **5 à 200 m de long** et **5 à 20 m de large**, réglables dans le PAW au pas de 5 m.
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
| `Src/BulldozerCleanModule.cs` | Le `PartModule` des trois lames : entrée PAW **« Passer la lame »**, curseurs **longueur** (5→200 m) et **largeur** (5→20 m) par pas de 5 m, **usure restante**, **coût de la passe**, **hauteur au sol** et **inclinaison** en direct. Construit la bande, relève les quads impactés, applique les règles de refus **et le quota**, débite la lame, enregistre l'action puis save+reload. `TryBuildBand` est **partagé** avec les deux overlays, qui doivent viser la bande exacte du clic |
| `Src/terrain/PassCost.cs` | Ce qu'une passe coûte : les cases qu'elle ferait passer de blanc à noir, **chacune imputée au seul quad qui la possède**. Repasser sur du propre est gratuit ; nettoyer un terrain sans cailloux est plein tarif |
| `GameData/BulldozerMod/Parts/*.cfg` | Les trois lames (BD-2 / BD-3 / BD-5), branchées sur leurs propres modèles |
| `GameData/BulldozerMod/Parts/*.mu` | Les maillages, **produits de build** : ne jamais les éditer, relancer le générateur |
| `tools/make_blades.py` | Génère les trois modèles et les exporte en `.mu`. Toute la géométrie dérive de la **largeur** ; c'est aussi le seul endroit où vivent la convention d'axes Blender→Unity et la garantie « rien sous le point d'attache » |
| `tools/render_blades.py` | Rend les trois lames en PNG en **important** le générateur — donc jamais une seconde description de la forme. Cadrage et focale **ajustés** à la géométrie, pour que les vues ne sortent pas du champ en silence quand les constantes changent. Sort dans `tools/renders/`, **non versionné** : ce sont des rendus de contrôle, à régénérer, pas du contenu de dépôt |
| `Src/parallax/ParallaxInterop.cs` | Tout ce qu'on fait avec Parallax : pose du decal, liste des scatters d'un corps, purge du registre statique. **Compilé contre `ParallaxContinued.dll`** : une évolution de son API casse le build, pas le vol |
| `Src/parallax/BulldozerScatterDecal.cs` | Le decal lui-même : sous-classe du PQSMod de Parallax, **attachée à un seul quad** (celui dont elle occupe le centre). `RotationFor` = source unique du `rot` du masque |
| `Src/parallax/ScatterHighlighter.cs` | L'aperçu, et **la seule chose qui dit la vérité** sur ce qu'une passe enlèvera : pour chaque quad en portée il construit le masque que l'injection construirait (`ScatterCleaner.RasterFor`) et pose au shader sa propre question (bilinéaire, `< 0.5`) à la position de chaque instance. Montre donc la pixelisation, les décrochements de couture et les quads pris par un autre mod. Ne voit que les scatters **collidables** — l'herbe part aussi, sans jamais avoir été surlignée, et seulement ceux que Parallax a **chargés** : c'est `BandTrace` qui couvre le reste de la bande |
| `Src/parallax/BandTrace.cs` | **La zone, peinte au sol** : les cases que la passe noircirait, quad par quad. C'est ce qui montre le **bout** d'une passe de 200 m, là où le surlignage des rochers ne peut structurellement rien montrer. Dessine les **cases**, jamais le rectangle — donc la pixelisation, les coutures et les quads pris par un autre mod. Ne porte plus que le **quoi** et le **quand** : la géométrie est dans `GroundSheet` |
| `Src/parallax/GroundSheet.cs` | **La géométrie de tout calque au sol** : mesurer des cases au raycast, les mailler, les dessiner dans le repère du corps. Partagée par le tracé de la passe et par les zones vertes, pour qu'ils ne puissent pas dessiner le même sol différemment. La sonde part de la hauteur **analytique du point mesuré**, pas d'une altitude unique pour la feuille : une feuille couvre un quad entier, sur lequel le relief dépasse la portée du rayon |
| `Src/parallax/CleanedZonesOverlay.cs` | **Les zones déjà nettoyées, en vert** (case à cocher de la fenêtre de debug). Lit les **masques vivants**, jamais les actions enregistrées : c'est là qu'est la valeur de debug — une action écartée à l'injection ou un quad pris par un autre mod se voient comme un **trou dans le vert**. Une feuille par quad, construite une fois puis gardée (une zone ne bouge pas), un seul quad maillé par frame |
| `Src/parallax/OverlayMaterial.cs` | Le matériau des deux aperçus, et **le seul endroit** où vit la liste de shaders : un shader de KSP transforme un calque en artefact d'espace écran (§5), et `Shader.Find` échoue en silence |
| `Src/terrain/BladeStance.cs` | Comment la lame est posée : hauteur du **tranchant** au-dessus du sol et angle de son axe **haut** avec la **normale du sol**, en un raycast. Mesuré contre le sol et non contre l'horizon — sur une pente, une lame bien posée suit la pente |
| `Src/terrain/SurfaceBand.cs` | La bande d'une passe : origine, direction, longueur, largeur — **tout en directions unitaires du repère du corps**, jamais de lat/lon. Porte `WidthStep` (= une case), le pas et le minimum des deux curseurs |
| `Src/terrain/MaskProjection.cs` | **Le repère d'un masque**, et rien d'autre : direction du sol ⇄ pixel, dans l'expression exacte du compute shader. Séparé de `BandRaster` parce que deux choses en ont besoin — une bande qu'on **prévoit**, et un masque qui **existe déjà** et qu'on relit |
| `Src/terrain/BandRaster.cs` | **Le cœur de la pixelisation** : la bande projetée dans le repère d'UN masque, la règle « toute case touchée », et l'échantillonnage bilinéaire + seuil `< 0.5` du shader. Une seule implémentation, utilisée par le tracé **et** par le highlight — deux versions feraient mentir l'aperçu |
| `Src/terrain/QuadLocator.cs` | Géométrie des quads : arête, demi-diagonale, et **quels quads-feuilles une passe touche** (en vol uniquement) : pré-sélection par calotte contenant la bande, puis test **exact** « au moins une case effacée » |
| `Src/terrain/ScatterMask.cs` | Le masque d'un decal (`Texture2D` R8 dimensionnée à 5 m/case) ; `DrawBand` sert aussi bien à créer qu'à fusionner |
| `Src/terrain/ScatterCleaner.cs` | Purge nos decals, **regroupe les passes par quad** (un decal chacun), `ResetAndWait` des corps touchés. `RasterFor` = point d'entrée unique pour « le masque que ce quad recevrait » |
| `Src/persistence/BulldozerAction.cs` | Une action `{ body, lat, lon, forward, length, width, maxLevel, QUAD*{ dir, halfDiag } }`. La **largeur y voyage** : rejouer une vieille passe doit utiliser la largeur avec laquelle elle a été demandée, pas celle du curseur au moment du chargement |
| `Src/persistence/BulldozerScenario.cs` | `ScenarioModule` (`AddToAllGames`) : source de vérité par save ; son `OnLoad` déclenche l'injection |
| `Src/ui/BulldozerDebugUI.cs` | **Le bouton de la barre d'app (vol uniquement) et la fenêtre qu'il ouvre.** Vol uniquement parce que ce que la fenêtre commute se dessine sur du terrain qui n'existe qu'en vol |
| `Src/ui/BulldozerDebugViewModel.cs` | L'état de la fenêtre, et le **seul** endroit où le réglage persisté et l'interrupteur statique du calque sont tenus en phase (le second doit être statique, son addon étant reconstruit à chaque changement de scène) |
| `Src/ui/ugui/**`, `Src/ui/styles/` | Les composants uGUI, un `Builder` + un `Controller` chacun (pattern `KSP-Shared`) |
| `Src/settings/BulldozerSettings.cs` | Les réglages **par installation** (`PluginData/bulldozer.cfg`) — jamais des données de partie : les zones nettoyées, elles, sont dans le `ScenarioModule` |
| `tools/create_icon.ps1` | Génère `GameData/BulldozerMod/icon.png` (38 px, silhouette blanche sur fond transparent, au style des mods frères). Produit de build : ne pas retoucher le PNG, relancer le script |
| `Src/diagnostics/QuadOverlay.cs` | **Outil de test** : affichage en vol des quads voisins (MOD+B) — voir ci-dessous |
| `Src/diagnostics/BulldozerScatterDiagnostic.cs` | **Outil de test** : dump complet de la chaîne dans `KSP.log`, 8 s après l'entrée en vol |
| `Src/diagnostics/BandDiagnostic.cs` | **Outil de test** (entrée PAW « Analyser la passe ») : dump lecture seule de tout ce qu'une passe déciderait. Conçu pour séparer trois pannes qui se ressemblent à l'écran — projection fausse, familles non surlignables, ou simplement une lame étroite face à des grappes. C'est lui qui a prouvé la visée (§5) et permis de chiffrer le bug du cap |

### Se placer sur une limite de quad (`QuadOverlay`)

Ouvert avec **MOD+B** en vol (MOD = shift droit sous Windows). Il liste les **4 quads-feuilles les plus proches**, avec la distance du vaisseau à leur **centre** et leur propriétaire éventuel :

- deux distances égales ⇒ le vaisseau est sur **l'arête** entre ces deux quads ;
- quatre distances égales ⇒ il est sur le **coin** où quatre quads se rejoignent ;
- la ligne `spread` donne directement l'écart entre la plus petite et la plus grande — la faire tendre vers 0 revient à se placer au coin.

La dernière ligne annonce combien de quads la **passe réglée dans le PAW** toucherait à cet endroit : c'est le nombre de decals qui seraient créés, donc la valeur à surveiller pour tester une passe à cheval.

**Supprimés** : `BulldozerFlattenMod` (le PQSMod de nivellement), `TerrainLeveler`, `BulldozerPlugin` (bouton toolbar, remplacé par le PAW) et `BulldozerDiagnosticPlugin` (dump d'altitudes mesh vs analytique, sans objet maintenant).

Acquis transverses conservés : le `ScenarioModule` est la **source de vérité par save** (les corps sont `DontDestroyOnLoad` → une injection runtime **fuit entre parties** sans purge) ; on **n'agit jamais sur un PQS vivant**, on injecte pendant l'écran de chargement, vaisseau *packed* ; on ne **repositionne jamais** un vaisseau en physique active.

Question ouverte mineure : le nom « Bulldozer » n'est plus tout à fait juste (on ne pousse plus de terre, on ramasse les cailloux). À trancher plus tard — un renommage coûte un dossier `GameData`, un `.csproj`, un dépôt.

## 5. Ce qui est validé — et les pièges qui coûtaient cher

> Un troisième piège, découvert le 2026-08-27, est documenté ailleurs parce qu'il vit dans une règle de refus : **la projection horizontale d'un axe de contrôle vertical n'est pas un cap**. Voir §2, « Règles de refus ».

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

- `origin check: lat/lon vs world position differ by **0,11 m**` → l'origine de la bande est bien sous le rover, malgré l'aller-retour lat/lon ;
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

**Le seuil de cap, chiffré.** Le même dump donne `vessel root .up` à fraction verticale **0,984** — c'est l'axe du point de contrôle « Haut ». Sa composante horizontale vaut `√(1−0,984²) = 0,178`, soit **5 % au-dessus de l'ancien seuil de 0,17** : il passait. Son cap valait **79,5°** (145° à côté du rover, donc bande hors champ) et « Inversé » **259,5°** (34,6° à droite du rover). C'est exactement le symptôme observé, au degré près — voir le piège en §2.

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

**Près du KSC, rien ne se nettoie.** Parallax y pose son propre decal, `ParallaxMaskKSC` (`radius = 1400` m, donc une portée de `atan(1400/600000) × 4` ≈ **5,6 km**), et un quad n'accepte qu'un seul decal : le nôtre est ignoré sur tous les quads déjà pris, silencieusement. Le log le nomme explicitement (`Quad already owned by 'ParallaxMaskKSC'`). Sans conséquence pour l'usage visé — on ne construit pas de base sur le KSC — mais à savoir pour ne pas croire à une panne.

**Le nettoyage peut être partiel selon l'instant.** Un essai a laissé fleurs et herbes éparses ; le diagnostic montrait alors que le quad portant le vaisseau **n'était pas** pris en charge (le plus proche connu de Parallax était à 752 m, au-delà des 651 m possibles pour un quad de 920 m d'arête). Les scatters de ce quad avaient donc été générés sans notre masque. Rejoué au même endroit, le résultat était parfait. **Piste** : un quad ne s'enregistre qu'à sa construction (`isBuilt` n'est remis à `false` que dans `Awake()`), donc tout quad déjà bâti au moment de l'injection est perdu pour la passe. À surveiller si le cas se reproduit.

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

## 6. Les lames, et ce qui reste à faire

### Les trois lames : ce qui a été retenu

Elles remplacent le patch ModuleManager qui posait le module sur tous les modules de commande (`GameData/BulldozerMod/BulldozerMod.cfg`, **supprimé**). Les valeurs sont fixées :

| | BD-2 « Éclaireuse » | BD-3 « Chantier » | BD-5 « Terrassier » |
|---|---|---|---|
| Largeur du modèle | 2 m | 3 m | 5 m |
| Masse | 1,5 t | 3,5 t | 8,0 t |
| Coût / `entryCost` | 2 400 / 8 000 | 6 000 / 20 000 | 15 000 / 45 000 |
| Nœud | `specializedConstruction` | `advMetalworks` | `metaMaterials` |
| **Cases** | **200** | **600** | **2 000** |
| kg par case | 7,5 | 5,8 | 4,0 |

**La largeur du modèle ne plafonne PAS la largeur de passe** — les trois gardent le curseur complet 5→20 m. Un bulldozer fait des allers-retours : une lame de 2 m qui dégage une bande de 20 m ne ment pas. Ce qui sépare les trois modèles est **combien** elles peuvent nettoyer, pas **en une fois**.

**Le vrai frein aux autoroutes est la masse, pas le quota.** Le quota borne UNE lame ; la masse borne le nombre de lames qu'on peut poser sur un corps. C'est pour ça que `kg/case` **descend** avec la taille : la progression technologique doit récompenser, sinon personne ne monte en gamme. Ce qu'il faut éviter reste l'**accumulation** : chaque quad nettoyé porte pour toute la session un `PQSMod` et sa `Texture2D` lisible (**~52 Ko par quad sur la Mune**, mesuré ; une passe touche 4 quads, d'où les « 206 Ko total » que `QuadOverlay` affiche), tous **reconstruits à chaque chargement** par `ScatterCleaner.InjectActions`, et tous parcourus à chaque build de quad. Le quota est donc un **garde-fou de performance déguisé en gameplay** : le joueur comprend « ma lame est usée », pas « le mod rame ».

**Ce que les quotas achètent.** Une case déjà noire étant gratuite, une zone compacte de S cases de côté coûte ≈ `S² + 2S` (l'aire, plus son périmètre payé une fois) : **~65 × 65 m**, **~115 × 115 m** et **~215 × 215 m**. Corollaire assumé : des passes **jointives** ne paient presque que leur surface, des passes éparpillées repaient leur périmètre à chaque fois. Une passe isolée de 200 × 5 m coûte ~85 à 100 cases, pas 40.

⚠️ **Le comptage ne se fait NI sur la surface du geste, NI sur la somme des cases effacées par quad.** Les deux sont faux, pour deux raisons différentes :

- **la surface du geste** (`longueur × largeur / 25`) ferait payer un second passage sur du terrain déjà propre — or c'est précisément ce qu'on veut gratuit ;
- **la somme par quad** surfacturerait d'un facteur 2 à 4. Un masque couvre l'emprise de son quad, qui **déborde** ce quad (`FootprintMargin`), donc une passe près d'un coin est réellement dessinée dans 2 à 4 masques. Le même geste coûterait alors quatre fois plus cher selon l'endroit où le joueur se tient — invisible pour lui, et injustifiable.

**La règle retenue** (`Src/terrain/PassCost.cs`) : compter les cases qui passent de blanc à noir, **et n'imputer chaque case qu'au quad qui la possède**. L'appartenance est exacte — les quatre demi-espaces de `QuadLocator.TryGetEdgePlanes`, portés par `QuadRef.edgePlanes` — et l'inverse de la projection est `BandRaster.DirectionOf`. Chaque parcelle de sol est donc comptée une fois, à **une case près par couture** traversée (les grilles voisines ne sont pas alignées : une case à cheval peut être comptée deux fois ou zéro).

Trois conséquences voulues : repasser au même endroit ne coûte rien ; une passe posée le long d'une déjà nettoyée ne paie que ce qu'elle ajoute ; et nettoyer un terrain **sans aucun scatter** coûte plein tarif, parce que ce qu'on dépense est le masque, pas les cailloux.

**Écarté le 2026-08-27 : ne facturer que les cases qui contiennent vraiment un rocher.** L'idée est naturelle — `ScatterHighlighter` lit `Parallax.CollisionManager.collisionData` et sait instance par instance ce qui tomberait, donc la donnée existe et le mapping instances → cases tiendrait en vingt lignes. Elle est refusée parce que **cette liste ne décrit pas le sol, elle décrit ce qui est chargé et collidable à cet instant** :

- **les familles non collidables passeraient gratuites** — sur la Mune `TinyRocks` et `SmallRocks` sont bien retirées mais absentes de `collisionData` ; sur Kerbin c'est l'herbe, la famille la plus dense ;
- **le prix dépendrait de la distance de chargement** : le highlight ne voit que les quads chargés (c'est la limite « viser à l'aveugle » ci-dessous), donc la même passe coûterait moins cher faite de loin, et changerait de prix selon le preset de détail. L'exploit s'écrit tout seul ;
- **le prix annoncé divergerait du prix débité**, le PAW recalculant 2×/s pendant que les scatters se chargent et se déchargent.

Et au-delà de la technique : **une instance est une grappe ancrée en un point et étalée sur 15-25 m**. Une case sans ancre n'est pas une case sans rocher — elle est souvent nettoyée par la chute d'une grappe voisine. « Cette case n'a rien » n'est pas une propriété qu'on peut lire honnêtement.

Ce qu'on garde à la place : un prix **déterministe et fonction du seul geste**. L'usure paie le **masque** — la vraie ressource rare — pas les cailloux. Et l'information que ce calcul donnerait au joueur, il l'a déjà : le highlight montre ce qui tombe, le coût s'affiche à côté, il peut lire « 65 cases pour trois cailloux » et déplacer sa passe avant de payer.

Le quota est un état de **pièce** (`cellsLeft`, `isPersistant` sur le PartModule), pas une donnée du `ScenarioModule` : il appartient à la lame, pas à la partie. Il est débité **avant** `HighLogic.CurrentGame.Updated()`, sinon le rechargement ramènerait une lame qui n'a pas payé.

**Ce que le joueur voit** : le coût de la passe réglée s'affiche en direct dans le PAW (rafraîchi 2×/s tant que la fenêtre est ouverte), à côté de l'usure restante. Coût > restant → refus chiffré, **sans rechargement** ; coût nul (tout est déjà propre) → refus aussi, pour la même raison : le rechargement est la partie chère d'une passe, pas la lame.

**Les lames collisionnent** (décision du 2026-08-31, `disableCollisions` **supprimé**) : une lame est modélisée au ras du sol, donc telle quelle elle laboure le terrain et cloue le rover sur place. **C'est le but** — le joueur doit la porter sur un **axe robotisé**, la lever pour rouler et la descendre pour travailler. Le mod n'a techniquement besoin d'aucun contact (le nettoyage se décide dans un masque) : la contrainte est de gameplay, et les deux règles de refus « lame trop haute / mal alignée » (§2) sont ce qui la rend obligatoire plutôt que décorative.

⚠️ **La version précédente passait les colliders en TRIGGER en vol** (jamais désactivés — un collider désactivé est invisible au `Physics.RaycastAll` de `Mouse.CheckHoveredPart`, et **la pièce perd tout son PAW** ; un trigger, lui, reste cliquable parce que le `queriesHitTriggers` d'Unity vaut `true` par défaut et que KSP ne le change jamais). À ressortir de git si l'on revient un jour sur la décision — mais alors il faut ressortir aussi le fait qu'une lame sans contact rend les deux seuils ci-dessus purement déclaratifs.

**Le joueur l'apprend dans l'éditeur, pas en jeu** : `GetInfo()` remplit le pavé « Lame de bulldozer » de l'infobulle avec la capacité, les bornes des deux curseurs et les deux seuils de pose — **tout lu depuis les constantes du module**, donc rien à resynchroniser. Les `description` des trois pièces répètent les deux seuils en prose, et **celles-là sont des copies** : changer `MaxClearance` ou `MaxTilt` oblige à rouvrir `Localization/*.properties` (un ⚠️ le rappelle au-dessus des constantes).

À savoir au passage : ni l'un ni l'autre état ne sort la lame du calcul de `Vessel.getLowestPoint()`, qui **réactive lui-même tous les colliders** le temps de sa mesure avant de restaurer leur état.

**Modèles : générés, plus empruntés au stock.** Vus en jeu le 2026-08-30 : les trois pièces se compilent, se posent, et le rendu convient. ⚠️ **Le cap n'est pas encore validé** — c'est le premier essai où il vient de `bladeFace` et non du point de contrôle.

Deux défauts trouvés à l'écran, tous deux corrigés — et tous deux dans les **raccords** entre solides, jamais dans la forme elle-même. Les deux règles générales sont dans le [CLAUDE.md parent](../CLAUDE.md), piège « deux solides qui se RECOUVRENT clignotent ».

1. **Zébrure mouvante sur le flanc** : les joues latérales recouvraient le tablier au lieu de s'y abouter, d'où des faces coplanaires de même normale que le test de profondeur ne peut pas départager. Le tablier s'arrête maintenant là où les joues commencent — forme inchangée, seul le pavage diffère.
2. **Deux rectangles au travers de la face de travail** : les bras pousseurs, enfoncés de 6 cm dans une tôle de 5, ressortaient devant. Aucune profondeur unique ne pouvait marcher, le tablier étant courbe : sur la hauteur d'un bras le dos recule de 3 à 7 cm, **plus que l'épaisseur**. Le bout des bras suit désormais la corde du dos (`arm_front`), qui recale les deux extrémités d'un coup et se vérifie contre la peau avant.

⚠️ **Ces deux défauts se mesurent, ils ne s'estiment pas.** Les deux fois, le raisonnement à la main donnait la mauvaise réponse : j'ai cru que le trou de la joue se réglait par un décalage de quelques millimètres (c'était un recouvrement à supprimer), et j'ai daté le débordement des bras du mauvais modèle avant de rejouer le profil en Python. Les contrôles utiles sont donc scriptés : rejouer `profile()` hors de Blender pour mesurer le bras contre les deux peaux, et l'audit de coplanarité sur les `.mu` produits.

Les trois lames ont leur propre maillage, produit par `tools/make_blades.py` : un tablier concave (Bézier quadratique), une lèvre de coupe amincie, deux joues latérales, deux bras pousseurs et une platine. **Toute la géométrie dérive de la largeur** (2 / 3 / 5 m) — hauteur `0,30 × W + 0,30`, épaisseurs, longueur des bras.

Trois choses qu'il a fallu établir plutôt que deviner, et qu'il ne faut pas re-instruire :

- **La convention d'axes de l'exporteur.** `io_object_mu` échange Y et Z à l'écriture, donc Blender (x,y,z) → Unity (x,z,y). Le générateur écrit en **X = largeur, Y = direction visée, Z = haut**. Conséquence exploitée : un objet Blender **sans rotation** ressort avec une rotation Unity identité, donc son `forward` Unity (+Z) est le +Y de Blender — c'est pourquoi `bladeFace` est un empty sans aucune rotation.
- **`node_attach` pointe à l'OPPOSÉ de la surface d'accrochage.** Établi sur la composition `LookRotation(hit.normal) × LookRotation(orientation)` d'`EditorLogic`, recoupée avec l'aérofrein stock (`node_attach = 0,0,-0.025, 0,0,1`, face de montage en −Z). D'où `0, 0, 0, 0, 0, 1, 1` : le +Z de la lame regarde à l'opposé du rover. ⚠️ Ce raisonnement n'est propre que pour une orientation selon ±Z ; selon ±X la composition de `LookRotation` donne l'inverse (les ailes stock en témoignent, `MainWing` et `sweptWing` ayant des signes opposés).
- **La contrainte « pas sous le plan des roues » est tenue par construction**, pas par la prudence du joueur : le nœud d'attache est au niveau du **tranchant** et rien du modèle ne descend sous lui. Comme le nœud est le point qui atterrit là où le joueur clique, et que ce clic est sur la caisse du rover donc au-dessus du contact des roues, la lame ne peut pas devenir la réponse de `getLowestPoint()`. `EDGE_DROP` échange cette garantie contre du look.
- **Un second nœud, d'empilement** (`node_stack_back`, ajouté le 2026-08-30 sur demande) : centrer la lame à la main est pénible, alors qu'elle se branche naturellement au **bout de la poutrelle** qui sert de châssis. Il est au centre de la platine, face à −Z, taille 1 comme les poutrelles stock, et `attachRules` passe de `0,1,0,0,1` à `1,1,0,0,1` — la lame devient empilable, mais rien ne peut toujours s'attacher **à** elle.

  ⚠️ **La garantie ci-dessus est une propriété de `node_attach` SEUL.** Le nœud d'empilement est **0,36 / 0,47 / 0,68 m au-dessus du tranchant** (BD-2 / BD-3 / BD-5), donc une lame branchée sur une poutre pend d'autant sous elle : c'est au joueur de mettre la poutre assez haut. Ces valeurs viennent du générateur, qui les imprime avec les autres lignes de `.cfg` — elles ne se retapent pas à la main, elles suivent la géométrie.

⚠️ **Ce n'est pas le tranchant qui descendait le plus bas** mais le **dessous de la lèvre** : l'épaisseur est reportée le long de la normale du profil, qui bascule vers le bas sur un tranchant incliné (−11 à −16 mm mesurés selon le modèle). Le décalage vertical est donc **mesuré** sur le profil construit, jamais supposé — c'est le genre d'erreur qui passe inaperçue à l'œil et fausse la garantie.

Vérifié en relisant les `.mu` : largeurs exactement 2 / 3 / 5 m, point le plus bas à `+0.000000` sur les trois, collider couvrant tout depuis z = 0, `bladeFace` présent avec rotation identité, UV présents, deux matériaux `KSP/Specular`.

**Les trois icônes vont se ressembler**, et c'est structurel : KSP normalise l'icône sur la **plus grande dimension** du modèle — ici la largeur, la seule chose qui sépare les trois lames (mécanisme et leviers dans le [CLAUDE.md parent](../CLAUDE.md), « Les icônes de l'éditeur sont générées au chargement »). Proportions normalisées : 1 : 0,46 : 0,43 / 1 : 0,41 : 0,36 / 1 : 0,37 : 0,30 — trop proches pour se distinguer dans la liste. Si ça gêne à l'usage, il faudra une différence de **forme** (nombre de bras, nervures), pas de taille. Corollaire rassurant au passage : le décentrage de nos modèles n'a **aucune** conséquence sur le cadrage de l'icône.

### Le reste
- ✅ **Fenêtre de debug — faite le 2026-08-31.** Un bouton dans la barre d'app (**vol uniquement** : ce qu'elle commute n'existe qu'en vol), une fenêtre `Bulldozer Debug` avec une seule case à cocher, **Afficher les zones déjà nettoyées**. Décochée par défaut, mais **persistée par installation** dans `PluginData/bulldozer.cfg`.

  Trois choses à retenir de sa mise en place :

  - **Elle a demandé une modif de `KSP-Shared`** (donc à commiter là-bas et à propager) : la zone du bandeau de titre à gauche du bouton « fermer » était **obligatoire** — `PopupBuilder` appelait `_titleBarBuilder.Build()` sans test. Elle est maintenant **facultative** ; sans builder (et avec `MonoBehaviour` en `T`), le bouton de fermeture reste seul contre le bord. Purement additif : les mods qui en fournissent un ne changent pas de comportement. C'est ce qui permet à cette fenêtre de n'afficher que son titre.
  - **Elle a fait entrer la lib uGUI de `KSP-Shared` dans ce mod.** Le `.csproj` ne compilait que trois fichiers partagés ; il prend maintenant `KSP-Shared\Src\**\*.cs`, ce qui a demandé 5 références Unity de plus (`UI`, `UIModule`, `InputModule`, `TextRenderingModule`, `ImageConversionModule`). Rien à faire côté `build.sh` : il recopiait déjà `KSP-Shared/GameData/Textures/`.
  - **Le vert lit les masques vivants, pas les actions.** C'est le choix structurant (voir `CleanedZonesOverlay`) : les deux sources ne diffèrent qu'aux endroits où quelque chose a échoué silencieusement, et ce sont précisément ceux qu'on veut voir.
  - **La géométrie du tracé rouge a été extraite dans `GroundSheet`** pour que le vert la réutilise. Le refactor a corrigé au passage une vraie limite : la sonde partait d'une altitude unique pour toute la feuille, ce qui ne pouvait pas tenir sur un quad entier — elle part maintenant de la hauteur analytique de chaque point mesuré.

  ⚠️ **Rien de tout ça n'a encore été vu en jeu** — ni la fenêtre, ni le vert, ni le tracé rouge qu'il partage désormais.

- ✅ **Viser à l'aveugle — fait le 2026-08-31** (`Src/parallax/BandTrace.cs`, même bascule PAW que le surlignage). Le tracé peint les **cases du masque**, pas le rectangle : mêmes règles que l'injection (`ScatterCleaner.RasterFor`), une case n'est peinte que par le quad qui la **possède** (`QuadLocator.Contains`, sinon l'alpha doublerait à chaque couture), et rien n'est peint sur un quad pris par un autre mod. Il suit le relief par **raycast sur le collider** (layer 15) et non par `GetSurfaceHeight` : l'analytique n'est exact qu'aux sommets du quad, à 60 m d'écart sur la Mune, donc un tapis posé dessus s'enterre dans chaque creux. ⚠️ **Pas encore vu en jeu.**
- **Retirer les outils de test** (`Src/diagnostics/`) : `QuadOverlay`, `BulldozerScatterDiagnostic` et `BandDiagnostic`. À garder **jusqu'à ce que `bladeTransform` soit validé en jeu** : c'est le premier essai où le cap ne vient plus du point de contrôle, et `BandDiagnostic` est justement ce qui compare `band forward` au navball. `BandDiagnostic` a payé son écot le 2026-08-27 (il a prouvé la visée et chiffré le bug du cap), et c'est le seul moyen de distinguer « le masque est ailleurs » de « la lame passe entre les cailloux ».
- **Détail terrain : exigence caduque.** On voulait imposer le preset maximum, d'abord pour la finesse (elle vient de la texture, pas du maillage : sans objet), puis parce que la taille des quads en dépend. Les deux raisons sont tombées : l'arête est **mesurée en vol** et voyage dans l'action, et un changement de preset est détecté par la garde `maxLevel` (l'action est alors ignorée avec un avertissement, plutôt qu'appliquée de travers). Reste à décider ce qu'on fait de ces actions orphelines — les recalculer à la première occasion en vol serait plus élégant que de les abandonner.
- **Bords de zone** : coupure franche ou dégradé dans le masque (le sampler est bilinéaire, un masque adouci suffirait). Sans objet à 5 m de large — une seule case, aucune place pour un dégradé — et de peu d'intérêt à 20 m : le bord perçu est celui des scatters survivants, déjà irrégulier à l'échelle de leur espacement.

### Questions ouvertes (pas des tâches)
- **Trois familles d'objets au sol, une seule traitée.** Constaté en jeu :
  1. **scatters Parallax** — ceux qu'on nettoie ;
  2. **scatters stock** (`PQSLandControl`, exclus par `removeScatter` → `vbData.allowScatter`) — non traités. Parallax respectant ce flag, un `PQSMod_MapDecal` stock serait la voie complémentaire, sans objet tant que Parallax est installé ;
  3. **surface features de Breaking Ground** — ni des scatters ni du décor : des objets de gameplay persistants, scannables au bras robotique. Vu en plein milieu d'une zone nettoyée, à 7 m du rover. **À ne probablement PAS supprimer** (c'est de la science), même si elles portent un collider et peuvent gêner une grue. À rouvrir seulement si ça devient un problème concret.
- **Effets de bord visuels** : le decal déclare `MeshColorChannel`, d'où la question d'un impact éventuel sur la couleur du terrain et sur les biomes. Rien de visible aux essais.

## 7. Archive : l'objectif abandonné (nivellement du terrain)

> **Plus aucun de ces fichiers n'existe** (`BulldozerFlattenMod`, `TerrainLeveler`, `BulldozerTerrainApplier`, `BulldozerVesselSeater`, `BulldozerPlugin`, `BulldozerDiagnosticPlugin`) — tout est récupérable dans git. Ne restent ici que les **constats vérifiés** qui coûteraient cher à re-établir, en une ligne chacun. La raison de la fermeture est en §3.

**Toujours utilisé aujourd'hui** (le nettoyage de scatters repose dessus) :

- **KSP reconstruit lui-même le PQS du corps actif à chaque entrée en vol**, vaisseau encore *packed* : `FlightDriver` → `PSystemSetup.SetPQSActive(pqs)` → `pqs.ForceStart()` → `StartSphere(force:true)` → `UpdateQuadsInit` → `CreateQuads()`, qui **détruit inconditionnellement** les 6 root quads et rejoue les mods (`PQS.cs:1351`). C'est la fenêtre dans laquelle on injecte, et c'est pour ça que l'injection se fait depuis `OnLoad` du `ScenarioModule`.
- **`ResetAndWait` ne fait en pratique que `ResetSphere()`** : sa coroutine d'attente est tuée juste après par le `StopCoroutine("ResetAndWaitCoroutine")` de `ForceStart` (`PQS.cs:925`), qui rebâtit synchronement. Le rebuild est donc fait par KSP, pas par nous.
- **`PSystemManager` est `DontDestroyOnLoad`** : les corps et leur `pqsController` vivent toute la session KSP. Toute injection runtime **fuit d'une partie à l'autre** si on ne purge pas au chargement.

**Spécifique au nivellement, conservé au cas où :**

- **Le grain PQS est le mur.** Une déformation ne peut déplacer que les sommets existants au niveau de subdivision max. Mesuré en jeu : Mune ~64 m, Kerbin ~100 m (~63 m au preset maximum), Minmus ~114 m, Eve ~190 m. Ça dépend de `maxLevel`, **pas** de la taille du corps. « Niveler 10 m autour du rover » est hors de portée du stock.
- **Sonder la hauteur après un flatten exige l'overload interne** `PQS.GetSurfaceHeight(Vector3d, bool overrideQuadBuildCheck)` avec `true` (par réflexion) : l'overload public passe `false`, `PQSMod_FlattenArea` sort alors sur sa barrière `quadActive` et renvoie la hauteur d'origine — probe faussé, faux négatif silencieux.
- **`PQSMod_FlattenArea`** : deux cutoffs par sommet (`angleQuadInclusion = 2 × angleOuter`, **puis** `angleOuter` — le premier n'est donc pas le bord de zone), barrière `!quadActive` conditionnée par `!overrideQuadBuildCheck`, cible **absolue** `flattenToRadius` calculée une fois dans `OnSetup`. Bords francs = `angleInner = angleOuter`.
- **Le re-seating au chargement est CONDITIONNEL.** `Vessel.GoOffRails()` (`Vessel.cs:2838`) met `skipGroundPositioning = true` — donc **aucune repose** — si les champs `PQSMin`/`PQSMax` de la save (`ProtoVessel.cs:2260`) sont non nuls et égaux au `minLevel`/`maxLevel` du PQS courant. Forcer `PQSMin = 0` dans la save de rechargement fait exécuter `CheckGroundCollision` et repose le vaisseau au raycast.
- **`Vessel.getLowestPoint()`** (`Vessel.cs:3101`) traite les colliders de roue (layers 26/27/30) par `bounds.min.z` puis `-= wheelCollider.radius; -= 0.15f` — une **approximation**, parce qu'un `WheelCollider` Unity est un raycast le long de l'axe de suspension et non une géométrie ; ses GameObjects sont désactivés quand le vaisseau est *packed* et doivent être réactivés pour mesurer.
- **Repositionner un vaisseau en physique active le disloque** : `CheckGroundCollision()` appelé directement → explosion ; `GoOnRails`/`GoOffRails` sur le vaisseau **actif** (qui ne se packe pas vraiment) → explosion aussi.
- **`PQSCache.PresetList`** (champ `public static`) donne le niveau de détail actif : `presetIndex`, `preset`, `presets` (ordonnés du moins au plus détaillé).
