# Le mécanisme : un masque de scatters par quad

Comment le mod supprime les rochers Parallax sans toucher au relief : le PQSMod d'exclusion, la texture masque, son repère, sa résolution, la construction d'une passe de lame, et la géométrie réelle des quads et des grappes de scatters.

> Fichier de contexte de **KSP-Mod-Bulldozer**. Le fil directeur du projet est dans [CLAUDE.md](CLAUDE.md).

## Objectif et mécanisme

**Supprimer les scatters sur une passe de lame devant le rover, de façon persistante, sans déformer le relief.**

### Le levier : le PQSMod d'exclusion de Parallax

Parallax n'utilise **pas** le `removeScatter` du stock pour ses propres scatters : il embarque son PQSMod, `PQSMod_MapDecalVertexRemoveScatter` ([source](file:///d:/kspmod-ext/Parallax-Continued/Mod%20Source/Parallax/PQS%20Mods/MapDecalVertexRemoveScatter.cs)). Ses propriétés, **lues dans le source puis confirmées en jeu** (voir [CLAUDE-validation.md](CLAUDE-validation.md)) :

- **il ne touche jamais la hauteur** : pas de `OnVertexBuildHeight`, il ne déclare que `requirements = MeshColorChannel`. L'exclusion sans déformation est **native**, pas un détournement ;
- il croise **deux axes** : le **quoi** — une liste `blockedScatters` de **types** de scatters (jamais des instances) — et le **où** — un **masque `colorMap` (`Texture2D`)** projeté sur l'emprise du decal ;
- le masque est échantillonné **dans le compute shader** : canal **`.r`**, seuil **`< 0.5` = bloqué**, `linearClampSampler` ([`TerrainScatters.compute:176`](file:///d:/kspmod-ext/Parallax-Continued/Assets/Shaders/Scatters/TerrainScatters.compute)).

Deux conséquences déterminantes :

- **La forme de la zone est libre.** `radius` ne définit qu'une **emprise maximale** ; à l'intérieur, c'est la texture qui décide, pixel par pixel. Cercle, carré, forme quelconque — au choix.
- **La finesse ne dépend pas du maillage PQS.** Le shader tourne **un thread par triangle**, tire un point candidat et échantillonne le masque **à la position de ce point** : la résolution effective est celle de la **texture**. Le grain de ~63 m entre sommets, qui condamnait l'objectif de nivellement ([CLAUDE-archive-nivellement.md](CLAUDE-archive-nivellement.md)), **ne s'applique donc pas au nettoyage** : une zone de 20 m se rend au mètre près (mesurée à 19,9 m en jeu, [CLAUDE-validation.md](CLAUDE-validation.md)).

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

**Pas d'arrondi à la puissance de deux.** C'est la seule vraie source d'imprécision sur les 5 m : arrondir 346 → 512 sur Kerbin donnerait des cases de **3,4 m** (32 % d'écart) quand 60 → 64 sur Gilly donnerait 4,7 m (6 %) — un décalage différent sur chaque corps. Avec la taille exacte, l'écart tombe sous le millième (le simple arrondi à l'entier), et une `Texture2D` NPOT en bilinéaire + clamp ne pose aucun problème. Et les 5 m **sont** un contrat de gameplay : les deux curseurs avancent d'une case à la fois, et le quota des lames se compte en cases ([CLAUDE-lames.md](CLAUDE-lames.md)). L'arrondi exact n'est donc pas un luxe.

**Un seul octet par pixel, et une seule copie.** `ScatterMask` ne garde **pas** de `byte[]` à côté de sa texture : il écrit dans les pixels de la `Texture2D` elle-même, via `GetRawTextureData<byte>()` — qui n'est pas une copie mais une **vue** (`NativeArray<byte>` construit par `ConvertExistingDataToNativeArray(GetWritableImageData(0), …, Allocator.None)`). Les chiffres ci-dessus sont donc `MaskSize²` octets, un point c'est tout. Deux conséquences à ne pas casser : la texture doit rester **lisible** (`Apply(false, false)`, jamais `makeNoLongerReadable: true`, sinon `GetRawTextureData` lève), et la vue se **relit à chaque usage** plutôt que se mettre en cache, sa validité n'étant garantie que jusqu'à la prochaine modification de la texture.

**La taille est dérivée, jamais persistée.** Elle se recalcule au chargement depuis le `halfDiag` du quad, puisque les masques sont reconstruits en rejouant les passes. La stocker exposerait à un changement de preset de détail (`maxLevel` change → l'arête change → la taille attendue change) — ce que le garde-fou `action.maxLevel` détecte et refuse déjà.

**La grille est propre à chaque quad.** Chaque masque a son origine et son orientation (`rot` dépend du centre du quad), donc les cases de deux quads voisins **ne sont pas alignées** : une zone à cheval peut faire un décrochement d'au plus une case (5 m) à la couture. C'est assumé — chercher à construire une grille globale se heurterait à la torsion mal conditionnée de `FromToRotation` près des pôles du corps, pour un gain nul sur du retrait de cailloux.

**On stocke le geste, jamais les cases.** Les cases sont la granularité de rasterisation dans le masque, pas une unité de persistance — une passe de 200 × 10 m fait déjà 80 cases, et c'est le même raisonnement qui avait fait rejeter le stockage des hauteurs par sommet. Une action porte donc `{ origine, direction, longueur, largeur }`. La largeur y **voyage** parce qu'elle est un réglage joueur : rejouer une vieille action doit utiliser la largeur avec laquelle elle a été demandée, pas celle du curseur au moment du chargement.

⚠️ Ce sont les dimensions de la bande **dessinée**, déjà amputées du jeu de grille (« Le jeu de 1 m ») — pas les valeurs des curseurs. C'est le bon choix : si la constante change un jour, les zones déjà enregistrées ne se déforment pas. Corollaire : la relecture ne doit **jamais** réappliquer le retrait.

### La passe de lame — et pourquoi la pixelisation devient le sujet

Le geste est un **rectangle** partant du **tranchant de la lame, dans la direction qu'elle regarde**, **longueur de 5 à 200 m** et **largeur de 5 à 20 m**, les deux au pas de 5 m (= une case de masque) et réglables dans le PAW. Défaut : 50 × 10 m.

⚠️ **La bande réellement tracée est 1 m plus courte et 1 m plus étroite que les curseurs** (`SurfaceBand.GridSlack`) — 50 × 10 dessine 49 × 9. Le pourquoi est plus bas (« Le jeu de 1 m ») ; ce qu'il faut retenir ici, c'est que **les curseurs ne décrivent pas la géométrie du rectangle** mais la largeur de couloir visée, et que c'est la bande **amputée** qui est enregistrée dans l'action.

**La passe appartient à la lame, pas au vaisseau** — origine *et* direction. C'est ce qui fait de la lame un outil qu'on **porte** plutôt qu'une propriété du rover : une lame déportée sur un bras nettoie le sol qu'elle survole vraiment, une lame montée à l'arrière envoie sa passe en arrière, et deux lames sur un même châssis font deux passes différentes au lieu de la même deux fois. L'origine est **le point même** que mesure `BladeStance` : ce qu'on vérifie être au ras du sol est exactement ce d'où la passe part.

Conséquence assumée : le sol **sous le rover**, derrière la lame, n'est plus nettoyé — un bulldozer pousse devant lui.

La direction vient de `bladeFace`, un empty du modèle sans aucune rotation (son `forward` Unity est le +Y de Blender, cf. [CLAUDE-lames.md](CLAUDE-lames.md)). **Repli** pour un modèle qui n'en déclare pas : le **cap du navball** — l'axe `+up` du point de contrôle (`NavBall` cible `ActiveVessel.ReferenceTransform`) — donc « control from here » vise alors la passe comme il pilote le rover. Dans les deux cas, si l'axe est plus vertical qu'horizontal la passe est **refusée** : sa projection horizontale ne veut plus rien dire (voir « Règles de refus » ci-dessous — c'est un piège qui a coûté cher).

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

Ce dernier point est exactement pourquoi le **highlight échantillonne le masque** et non le rectangle (`ScatterHighlighter`, cf. [CLAUDE-code.md](CLAUDE-code.md)) : aucune approximation géométrique ne le montrerait. Combiné aux **grappes** (§ PIÈGE ci-dessous), qui laissent debout des blocs ancrés jusqu'à 25 m à côté, la conséquence assumée est qu'une passe étroite ne dégage pas un couloir propre au premier essai — le joueur le **voit** avant de valider, et repositionne.

### Le jeu de 1 m : rendre le compte de cases atteignable (2026-09-02)

Avec la règle « toute case touchée », une bande de largeur exactement `5N` ne couvre `N` colonnes que si ses bords tombent **pile** sur des frontières de cases — une fenêtre de **mesure nulle**, donc jamais atteinte en flottant. Le PAW annonçait ainsi une valeur **structurellement inatteignable** : 5 m demandés donnaient toujours 10 m, et 50 × 10 donnait toujours 11 × 3 cases.

**On retire donc 1 m** à la longueur et à la largeur (`SurfaceBand.GridSlack`). La fenêtre de phase passe de 0 à **1 m sur 5**, et surtout le **pire cas ne bouge pas** — `N+1` colonnes dans les deux cas. Ce n'est donc pas un arbitrage, c'est un gain strict.

**Ce que ça achète, chiffré** : sur une passe de 50 × 10 m, le plancher de coût tombe de `11 × 3 = 33` à `10 × 2 = 20` cases, soit **39 %**. L'alignement cesse d'être une coquetterie — il paie en quota, et le PAW le montre en direct.

**Le joueur vise avec l'aperçu, et il a deux indicateurs distincts** :

- **l'angle** — le bord en escalier de `BandTrace` **devient droit** quand le cap s'aligne sur la grille. Signal binaire, très lisible sur du terrain (cf. `docs/10-Preview.png`) ;
- **la phase** — le **coût en cases** du PAW tombe à son plancher. C'est un compteur exact, rafraîchi 2×/s ; sur terrain vierge il donne exactement le nombre de cases couvertes.

**Domaine de validité, à ne pas surestimer.** La fenêtre de 1 m doit absorber la dérive `L × sin θ` sur toute la longueur :

| Longueur | Précision de cap requise |
|---|---|
| 5 m | 11,5° |
| 20 m | 2,9° |
| 50 m | 1,15° |
| 200 m | 0,29° |

Le bénéfice est donc réel jusqu'à ~20-50 m et purement théorique à 200 m — où le retrait ne fait plus que coûter 1 m au bout (0,5 %). À quoi s'ajoute que **la grille appartient à UN quad** : `rot` dérive de `FromToRotation(centre du quad, up)`, donc le quad voisin a une grille **tournée**, et une bande qui traverse une couture est requantifiée de l'autre côté quel que soit le soin du joueur. L'alignement n'est tenable qu'à l'intérieur d'un quad.

**Ce qu'on perd** : la bande est plus étroite que la lame. Sur une BD-5 (5 m) réglée à 5 m, le cas *bien aligné* peut laisser jusqu'à **20 %** de la trace réelle de la lame debout, contre ~12 % avant. Cosmétique au regard des grappes de 15-25 m — la métrique honnête reste « la largeur de sol qui paraît propre ».

⚠️ **Le retrait est appliqué EXACTEMENT UNE FOIS**, dans `BulldozerCleanModule.TryBuildBand`, le seul endroit où le geste du joueur devient une bande (clic, prix, aperçu, tracé et diagnostic passent tous par là). **Jamais dans `SurfaceBand.TryCreate`** : le chemin de relecture (`BulldozerAction.ToBand`) y passe aussi, et comme l'action enregistre `band.length`/`band.width` — la bande **déjà** amputée — le retrait y serait réappliqué **à chaque chargement**, rognant 1 m de plus à chaque fois. C'est le piège à ne pas réintroduire.

Deux détails de géométrie : le mètre est retiré **au bout** en longueur (l'origine est le tranchant, le point que le joueur vise) et **centré** en largeur (`BandRaster` travaille sur `width × 0,5` de part et d'autre de l'axe, donc 0,5 m de chaque côté sans rien à écrire).

**Pourquoi 1 m et pas autre chose** : la fenêtre doit dépasser confortablement la précision de placement d'un rover au pas (de l'ordre du décimètre) sans amputer la case. 0,5 m serait pénible à viser, 2 m mangerait 40 % de la case pour un confort qu'on n'a pas besoin d'acheter.

### Ce que la multiplicité des masques ne coûte PAS

Une passe posée près d'un coin de quad doit être dessinée dans **4 masques** distincts. Deux craintes légitimes, toutes deux infondées — notées ici pour ne pas les ré-instruire :

- **Les 4 masques sont d'accord au demi-millimètre.** Chaque decal projette orthographiquement sur *son* plan tangent ; pour un point à distance angulaire `θ` du centre du decal, l'erreur vaut `R·θ³/6`, soit **0,45 mm** au coin d'un quad de Kerbin. Aucun décrochement visible ne viendra des repères — seulement, au pire, de la quantification en pixels (une case), là où le bord de la zone croise une frontière de quad.
- **Le bord n'est pas un escalier.** `FilterMode.Bilinear` + seuil `< 0.5` dans le shader reconstruisent la frontière à la **demi-résolution du pixel**, donc au milieu d'une case et non sur sa grille.

Et surtout : **le masque ne s'affiche jamais**. Il décide, rocher par rocher, lequel disparaît. Le bord perçu est celui des scatters survivants — déjà irrégulier à l'échelle de leur espacement. Une quantification à 5 m par-dessus une distribution pseudo-aléatoire à 2-3 m se lit comme une clairière aux bords naturels, ce qui est le rendu recherché.

**Troncature sur les petits corps (sans conséquence, et vérifiée).** Sur Gilly, `FootprintMargin = 1.1` ne laisse que ~13 m de marge autour d'un quad de 190 m : une passe de 200 m **sort du masque**, systématiquement puisqu'elle est plus longue qu'un quad, et `DrawBand` la clampe. Constaté en jeu le 2026-08-27 ([CLAUDE-validation.md](CLAUDE-validation.md)) : `band px start=(-10.2,35.5) end=(26.7,19.5)` sur un masque de 60 px, la bande sortant des deux côtés. C'est inoffensif — la partie tronquée est hors du quad, donc hors de la zone que ce decal contrôle, et le masque du quad voisin l'a dessinée en entier de son côté. La marge de 1,1 ne sert qu'à garantir que le carré contient le quad malgré la rotation, et pour ça elle suffit toujours.

### Comment un decal se limite à son quad

Le prix de l'architecture est mécanique : **une passe à cheval crée plusieurs decals**, un par quad touché, chacun portant sa portion de la bande. C'est le même code que la fusion — une seule fonction « dessiner la bande dans le masque de ce quad ».

**Ce qui rend l'architecture possible** : `BulldozerScatterDecal` **override `OnQuadPreBuild`**. Sans ça, un decal de ~700 m de rayon capturerait tout un cône de 2,8 km (le ×4) et empêcherait les decals voisins de s'enregistrer — le problème serait juste déplacé. Notre override refuse tout quad qui n'est pas le sien :

```csharp
if (quad.subdivision != sphere.maxLevel) return;   // seuls les quads-feuilles portent des scatters
if (AngleTo(quad) > CenterTolerance) return;       // et seulement CELUI dont je suis le centre
```

Deux conséquences à ne pas perdre de vue :

- le decal étant **exactement** au centre de son quad, l'angle vaut zéro : le clamp avant `Acos` ([CLAUDE-validation.md](CLAUDE-validation.md), PIÈGE 1) n'est pas un correctif de confort, c'est **la condition de fonctionnement** ;
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

