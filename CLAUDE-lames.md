# Les trois lames : modèles, variantes, et ce qui reste à faire

Les pièces elles-mêmes : géométrie retenue pour BD-2/BD-3/BD-5, plusieurs lames sur un même vaisseau, les deux livrées, et le reste à faire.

> Fichier de contexte de **KSP-Mod-Bulldozer**. Le fil directeur du projet est dans [CLAUDE.md](CLAUDE.md).

## Les lames, et ce qui reste à faire

### Les trois lames : ce qui a été retenu

Elles remplacent le patch ModuleManager qui posait le module sur tous les modules de commande (`GameData/KSPModBulldozer/KSPModBulldozer.cfg`, **supprimé**). Les valeurs sont fixées :

| | BD-2 « Éclaireuse » | BD-3 « Chantier » | BD-5 « Terrassier » |
|---|---|---|---|
| Largeur du modèle | 2 m | 3 m | 5 m |
| Masse | 1,5 t | 3,5 t | 8,0 t |
| Coût / `entryCost` | 2 400 / 8 000 | 6 000 / 20 000 | 15 000 / 45 000 |
| Nœud | `specializedConstruction` | `advMetalworks` | `metaMaterials` |
| **Cases** | **200** | **600** | **2 000** |
| kg par case | 7,5 | 5,8 | 4,0 |

**La largeur du modèle ne plafonne PAS la largeur de passe** — les trois gardent le curseur complet 5→20 m. Un bulldozer fait des allers-retours : une lame de 2 m qui dégage une bande de 20 m ne ment pas. Ce qui sépare les trois modèles est **combien** elles peuvent nettoyer, pas **en une fois**.

**Le vrai frein aux autoroutes est la masse, pas le quota.** Le quota borne UNE lame ; la masse borne le nombre de lames qu'on peut poser sur un corps. C'est pour ça que `kg/case` **descend** avec la taille : la progression technologique doit récompenser, sinon personne ne monte en gamme. Ce qu'il faut éviter reste l'**accumulation** : chaque quad nettoyé porte pour toute la session un `PQSMod` et sa `Texture2D` lisible (**~52 Ko par quad sur la Mune**, mesuré ; une passe touche 4 quads, d'où les « 206 Ko total » que le relevé de la fenêtre de debug affiche), tous **reconstruits à chaque chargement** par `ScatterCleaner.InjectActions`, et tous parcourus à chaque build de quad. Le quota est donc un **garde-fou de performance déguisé en gameplay** : le joueur comprend « ma lame est usée », pas « le mod rame ».

**Ce que les quotas achètent.** Une case déjà noire étant gratuite, une zone compacte de S cases de côté coûte ≈ `S² + 2S` (l'aire, plus son périmètre payé une fois) : **~65 × 65 m**, **~115 × 115 m** et **~215 × 215 m**. Corollaire assumé : des passes **jointives** ne paient presque que leur surface, des passes éparpillées repaient leur périmètre à chaque fois. Une passe isolée de 200 × 5 m coûte ~85 à 100 cases, pas 40.

**Et l'alignement paie.** Depuis le jeu de 1 m ([CLAUDE-masque-decals.md](CLAUDE-masque-decals.md)), une passe posée dans l'axe de la grille tombe sur son plancher de cases : **20 au lieu de 33** sur un 50 × 10 m, soit ~39 %. Le quota récompense donc le soin, et le joueur lit le gain en direct dans le PAW. Les surfaces ci-dessus restent des ordres de grandeur pour une zone bien faite — ce qui diminue est le **gâchis de désalignement**, pas la surface utile.

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

**Ce que le joueur voit** : le coût de la passe réglée s'affiche en direct dans le PAW (rafraîchi 2×/s tant que la fenêtre est ouverte), à côté de l'usure restante — ou, quand un decal d'un autre mod tient un des quads visés, **la zone qui le réserve à la place du prix**, le bouton disparaissant avec (cf. [CLAUDE-regles-passe.md](CLAUDE-regles-passe.md)). Coût > restant → refus chiffré, **sans rechargement** ; coût nul (tout est déjà propre) → refus aussi, pour la même raison : le rechargement est la partie chère d'une passe, pas la lame.

**Et le clic demande confirmation** (2026-09-01) — un `PopupDialog` stock, comme KSP-ReRoot pour le même geste. La raison n'est pas la prudence en général : une passe dépense deux choses **irréversibles pour le joueur**, l'usure de la lame et un rechargement de la partie, et aucune des deux ne se lit sur le bouton. La boîte annonce donc les trois nombres qui décident (cases consommées, restantes, restantes après) et le rechargement, avec **Annuler** en face.

Trois points de construction :

- **Le clic est coupé en deux** : `PrepareAndConfirm` (tous les refus, le prix) et `Commit` (débit, enregistrement, save+reload). Les refus tombent donc **avant** la boîte — on ne fait pas confirmer une passe qui va être refusée.
- **La boîte applique l'instantané qu'elle a annoncé**, pas une passe re-mesurée : bande, quads, coût et lat/lon sont capturés dans `PrepareAndConfirm` et passés à `Commit`. Le jeu continue de tourner derrière un `PopupDialog` stock, donc le rover peut dériver de quelques centimètres — le prix affiché reste le prix débité.
- **Le try/catch est sur les deux moitiés** (`Guarded`), parce que `Commit` part d'un bouton de la boîte, longtemps après le retour de `ClearScatters` : un try/catch autour du clic ne serait plus sur la pile.

⚠️ **Un popin `KSP-Shared` ne convient pas ici** : il lui faut une fenêtre à nous pour vivre dedans, et un PAW n'en est pas une. D'où le `PopupDialog` stock, dont le label « Annuler » vient quand même de la clé partagée `commonCancel`.

### Plusieurs lames sur un même vaisseau (2026-08-31)

Rien ne l'interdit, ni deux lames boulonnées sur un rover, ni deux rovers dockés qui en portent une chacun. **Chaque lame vise et s'use pour elle-même**, et c'est ce qui rend le montage lisible plutôt qu'ambigu :

| Ce qui décide | Porté par |
|---|---|
| longueur, largeur, usure restante | la pièce (`isPersistant`) |
| hauteur au sol, inclinaison (les deux refus) | la pièce |
| **origine et direction de la passe** | **la pièce** |
| l'aperçu — **une seule lame à la fois** | `ScatterHighlighter.Source` |

Deux PAW peuvent donc être épinglés côte à côte avec des réglages, des coûts, des usures et des poses **indépendants**. Chaque fenêtre ouverte recalcule son coût 2×/s, et ce calcul parcourt le registre de quads de Parallax : deux fenêtres, deux fois le travail.

**L'aperçu, lui, est exclusif** — et c'est un arbitrage de coût, pas d'ergonomie : une prévisualisation parcourt le registre de quads, rasterise la bande dans chaque quad touché et teste **chaque instance chargée** contre le masque obtenu, à chaque frame. L'allumer sur une lame l'éteint donc sur les autres, dont le PAW **redérive son propre libellé** de `ScatterHighlighter.Source` (test hors de la garde « PAW ouvert », pour que le libellé soit juste dès la frame où la fenêtre s'ouvre).

⚠️ **`Source` est aussi la visée des calques** : la lame en aperçu est celle qu'ils suivent, avec **ses** longueur et largeur. C'est la seule notion — il n'y a plus de « dernière lame touchée », plus de `SelectedLength`/`SelectedWidth`, et plus de surcharge `TryBuildBand(Vessel, …)` : **une bande se construit toujours depuis une lame**. Conséquences à connaître : un aperçu lancé depuis le PAW d'un rover **voisin** montre bien la passe de ce rover-là ; le relevé de quads n'a plus rien à annoncer sans aperçu actif (il le dit) ; et l'aperçu **s'éteint au changement de scène**, une lame ne survivant pas à un rechargement — donc après une passe, il faut le rallumer.

*C'était un vrai bug jusqu'au 2026-08-31* : la direction venait de `Selected` alors que tout le reste venait de la pièce. Une lame avant et une lame arrière, un clic sur celle de devant sans toucher aux curseurs, et la bande partait **à 180°**. Le facteur aggravant : on validait la pose de la lame A (bien posée) pour viser avec la lame B (levée), donc les deux règles de refus devenaient contournables. Corollaire moins visible : après un **undocking**, `Selected` partait avec l'autre moitié et la visée retombait silencieusement sur le point de contrôle.

**Le vaisseau de la lame, jamais l'actif.** Un PAW s'ouvre sur une pièce d'un vaisseau voisin non contrôlé : le coût et la bande se calculent donc sur `part.vessel`. Seul le `focusIdx` du rechargement reste sur `FlightGlobals.ActiveVessel` — nettoyer depuis le PAW du voisin ne doit pas rendre la main sur un autre rover.

**Côté gameplay, rien à durcir.** Poser une seconde lame, c'est **acheter de la capacité en payant de la masse** — le geste le plus banal de KSP (deux batteries, deux réservoirs). Le barème `kg/case` décroissant fait que l'empilement reste *possible mais moins bon* que la montée en gamme : 2 × BD-2 = 3 t pour 400 cases contre 3,5 t pour 600 sur une BD-3. Les quotas ne sont **pas mutualisés** (une passe à 250 cases est refusée sur une BD-2 même s'il y en a deux à bord — il faut la couper en deux) : « ma lame est usée » reste une propriété de la lame, pas un réservoir de vaisseau.

Le seul montage sans récompense est **deux lames côte à côte** : la largeur du modèle ne plafonnant pas la largeur de passe, elles ne couvrent pas plus large. C'est assumé (un bulldozer fait des allers-retours), mais le joueur peut s'y attendre.

**Les lames collisionnent** (décision du 2026-08-31, `disableCollisions` **supprimé**) : une lame est modélisée au ras du sol, donc telle quelle elle laboure le terrain et cloue le rover sur place. **C'est le but** — le joueur doit la porter sur un **axe robotisé**, la lever pour rouler et la descendre pour travailler. Le mod n'a techniquement besoin d'aucun contact (le nettoyage se décide dans un masque) : la contrainte est de gameplay, et les deux règles de refus « lame trop haute / mal alignée » ([CLAUDE-regles-passe.md](CLAUDE-regles-passe.md)) sont ce qui la rend obligatoire plutôt que décorative.

⚠️ **La version précédente passait les colliders en TRIGGER en vol** (jamais désactivés — un collider désactivé est invisible au `Physics.RaycastAll` de `Mouse.CheckHoveredPart`, et **la pièce perd tout son PAW** ; un trigger, lui, reste cliquable parce que le `queriesHitTriggers` d'Unity vaut `true` par défaut et que KSP ne le change jamais). À ressortir de git si l'on revient un jour sur la décision — mais alors il faut ressortir aussi le fait qu'une lame sans contact rend les deux seuils ci-dessus purement déclaratifs.

**Le joueur l'apprend dans l'éditeur, pas en jeu** : `GetInfo()` remplit le pavé « Lame de bulldozer » de l'infobulle avec la capacité, les bornes des deux curseurs et les deux seuils de pose — **tout lu depuis les constantes du module**, donc rien à resynchroniser. Les `description` des trois pièces répètent les deux seuils en prose, et **celles-là sont des copies** : changer `MaxClearance` ou `MaxTilt` oblige à rouvrir `Localization/*.properties` (un ⚠️ le rappelle au-dessus des constantes).

À savoir au passage : ni l'un ni l'autre état ne sort la lame du calcul de `Vessel.getLowestPoint()`, qui **réactive lui-même tous les colliders** le temps de sa mesure avant de restaurer leur état.

**Modèles : générés, plus empruntés au stock.** Vus en jeu le 2026-08-30 : les trois pièces se compilent, se posent, et le rendu convient. ⚠️ **Ni le cap ni l'origine ne sont encore validés en jeu** — c'est le premier essai où le cap vient de `bladeFace` et non du point de contrôle, et (depuis le 2026-08-31) où la passe part du **tranchant** et non du centre du vaisseau. `BandDiagnostic` mesure les deux : `blade facing` contre le navball, `origin check` contre le tranchant.

Deux défauts trouvés à l'écran, tous deux corrigés — et tous deux dans les **raccords** entre solides, jamais dans la forme elle-même. Les deux règles générales sont dans le [CLAUDE.md parent](../CLAUDE.md), piège « deux solides qui se RECOUVRENT clignotent ».

1. **Zébrure mouvante sur le flanc** : les joues latérales recouvraient le tablier au lieu de s'y abouter, d'où des faces coplanaires de même normale que le test de profondeur ne peut pas départager. Le tablier s'arrête maintenant là où les joues commencent — forme inchangée, seul le pavage diffère.
2. **Deux rectangles au travers de la face de travail** : les bras pousseurs, enfoncés de 6 cm dans une tôle de 5, ressortaient devant. Aucune profondeur unique ne pouvait marcher, le tablier étant courbe : sur la hauteur d'un bras le dos recule de 3 à 7 cm, **plus que l'épaisseur**. Le bout des bras suit désormais la corde du dos (`arm_front`), qui recale les deux extrémités d'un coup et se vérifie contre la peau avant.

⚠️ **Ces deux défauts se mesurent, ils ne s'estiment pas.** Les deux fois, le raisonnement à la main donnait la mauvaise réponse : j'ai cru que le trou de la joue se réglait par un décalage de quelques millimètres (c'était un recouvrement à supprimer), et j'ai daté le débordement des bras du mauvais modèle avant de rejouer le profil en Python. Les contrôles utiles sont donc scriptés : rejouer `profile()` hors de Blender pour mesurer le bras contre les deux peaux, et l'audit de coplanarité sur les `.mu` produits.

Les trois lames ont leur propre maillage, produit par `tools/make_blades.py` : un tablier concave (Bézier quadratique), une lèvre de coupe amincie, deux joues latérales, deux bras pousseurs et une platine. **Toute la géométrie dérive de la largeur** (2 / 3 / 5 m) — hauteur `0,30 × W + 0,30`, épaisseurs, longueur des bras.

Trois choses qu'il a fallu établir plutôt que deviner, et qu'il ne faut pas re-instruire :

- **La convention d'axes de l'exporteur.** `io_object_mu` échange Y et Z à l'écriture, donc Blender (x,y,z) → Unity (x,z,y). Le générateur écrit en **X = largeur, Y = direction visée, Z = haut**. Conséquence exploitée : un objet Blender **sans rotation** ressort avec une rotation Unity identité, donc son `forward` Unity (+Z) est le +Y de Blender — c'est pourquoi `bladeFace` est un empty sans aucune rotation.
- **`node_attach` pointe à l'OPPOSÉ de la surface d'accrochage.** Établi sur la composition `LookRotation(hit.normal) × LookRotation(orientation)` d'`EditorLogic`, recoupée avec l'aérofrein stock (`node_attach = 0,0,-0.025, 0,0,1`, face de montage en −Z). D'où `0, 0, 0, 0, 0, 1, 1` : le +Z de la lame regarde à l'opposé du rover. ⚠️ Ce raisonnement n'est propre que pour une orientation selon ±Z ; selon ±X la composition de `LookRotation` donne l'inverse (les ailes stock en témoignent, `MainWing` et `sweptWing` ayant des signes opposés).
- **La contrainte « pas sous le plan des roues » est tenue par construction**, pas par la prudence du joueur : le nœud d'attache est au niveau du **tranchant** et rien du modèle ne descend sous lui. Comme le nœud est le point qui atterrit là où le joueur clique, et que ce clic est sur la caisse du rover donc au-dessus du contact des roues, la lame ne peut pas devenir la réponse de `getLowestPoint()`. `EDGE_DROP` échange cette garantie contre du look.
- **Un nœud d'empilement, en haut du dos** (`node_stack_back` le 2026-08-30, remplacé par `node_stack_backTop` le 2026-09-01) : centrer la lame à la main est pénible, alors qu'elle se branche naturellement au **bout de la poutrelle** — ou de la **charnière** — qui la porte. Il fait face à −Z, taille 1 comme les poutrelles stock, et `attachRules` passe de `0,1,0,0,1` à `1,1,0,0,1` — la lame devient empilable, mais rien ne peut toujours s'attacher **à** elle.

  **Sa hauteur paie une gêne mesurée en jeu (2026-09-01).** Le premier nœud était au centre du bracket des bras, dans le tiers inférieur : une lame accrochée par lui ne pendait que de **0,36 / 0,47 / 0,68 m**. Or la charnière est montée **haut**, pour ne pas gêner la progression du rover — donc, bras à l'horizontale, le tranchant reste en l'air. Il fallait incliner la charnière (**30°** sur le montage BD-5 qui a servi de repère), ce qui **emporte la lame** dans la rotation, ce qui oblige à la **recorriger de 30°** en sens inverse : deux rotations à régler au lieu d'aucune, et un **coude visible** entre le bras et la lame.

  Une **plaque de dos** monte donc du bracket jusqu'au-dessus, et porte le nœud à **0,75 / 0,98 / 1,45 m** : le pendage **double**. Comme redresser une charnière fait remonter son extrémité de `L × sin 30°`, ce gain couvre un bras (pivot → accroche) de **0,77 / 1,03 / 1,54 m** — au-delà, il reste un résidu à rattraper comme avant.

  ⚠️ **Le nœud bas a été supprimé le même jour** : deux bulles vertes pour un seul usage réel, et le joueur peut toujours décaler la lame à la main. Le nom `...Top` lui survit — le renommer **détacherait la lame sur les vaisseaux déjà construits**, pour rien.

  **La plaque est dimensionnée sur la CHARNIÈRE, pas sur la lame.** C'est la seule cote du modèle qui ne dérive pas de la largeur, et c'est structurel : une charnière a la même taille quelle que soit la lame, donc sans plancher absolu elle déborde de la plaque sur les petits modèles (vu en jeu). Le plancher vient du **G-11 stock** : section **0,782 × 0,622 m**, mesurée dans `hinge_01.mu` en composant les transformations locales et en prenant la section au **plan d'accostage**.

  ⚠️ **Pas la boîte englobante brute** : la pièce est décalée dans son `.mu` (un `y` autour de −8,8) et ses sous-objets portent des transformations, donc une lecture naïve des sommets ne veut rien dire. Repère utile au passage, mesuré de la même façon : **G-00 = 0,26 × 0,21 m** (tient largement dedans), et les charnières alligator vont de 1,05 × 1,49 à 2,09 × 2,99 m — hors de portée d'une plaque, et jamais la cible.

  La plaque prend donc le **max** entre ce que la lame suggère (45 % de la largeur du bracket) et ce que la charnière exige : **0,84 × 0,52 / 0,85 × 0,59 / 1,36 × 0,79 m**. Deux conséquences assumées :

  - sur BD-2 et BD-3 elle **dépasse le haut du tablier** (de 16 et 9 cm), donc le box collider est ajusté sur **la plaque** et non plus sur le seul tablier ;
  - sur ces deux mêmes modèles, la moitié basse de la charnière descend **sous** la plaque — c'est le **bracket**, plus large, qui est derrière. Le générateur le **vérifie par le calcul** plutôt que de s'en remettre à l'œil : une charnière qui surplomberait le vide est exactement le défaut que la plaque corrige.

  ⚠️ La plaque est **aboutée** au bracket et jamais enfoncée : les deux partagent les plans `y = 0` et `y = tp`, donc tout recouvrement remettrait deux faces coplanaires de même normale l'une contre l'autre — la zébrure du [CLAUDE.md parent](../CLAUDE.md). Une première version à **pleine largeur** a été écartée au rendu : elle masquait tout le dos de la lame.

  **L'autre forme essayée** — remonter les **bras pousseurs** eux-mêmes, rendue et comparée le 2026-09-01 — donnait exactement le même pendage avec une silhouette plus épurée, mais mettait les bras dans le tiers **supérieur**, où un bulldozer ne pousse pas.

  ⚠️ **La garantie ci-dessus est une propriété de `node_attach` SEUL.** Une lame branchée sur une poutre pend sous elle de la hauteur du nœud : c'est au joueur de mettre la poutre assez haut. Ces valeurs viennent du générateur, qui les imprime avec les autres lignes de `.cfg` — elles ne se retapent pas à la main, elles suivent la géométrie.

⚠️ **Ce n'est pas le tranchant qui descendait le plus bas** mais le **dessous de la lèvre** : l'épaisseur est reportée le long de la normale du profil, qui bascule vers le bas sur un tranchant incliné (−11 à −16 mm mesurés selon le modèle). Le décalage vertical est donc **mesuré** sur le profil construit, jamais supposé — c'est le genre d'erreur qui passe inaperçue à l'œil et fausse la garantie.

Vérifié en relisant les `.mu` : largeurs exactement 2 / 3 / 5 m, point le plus bas à `+0.000000` sur les trois, collider couvrant tout depuis z = 0 (plaque de dos comprise), `bladeFace` présent avec rotation identité, UV présents, deux matériaux `KSP/Specular`.

### Deux livrées : acier brut et inox poli (2026-09-01)

Le gris d'origine est assez sombre. Une seconde peinture, plus brillante, est offerte **sans ajouter de pièce** : c'est une variante `ModulePartVariants`, choisie par les pastilles de couleur dans le PAW en éditeur. Aucun modèle, aucune texture, aucun code — que du `.cfg`.

| | `Steel` — acier brut (base) | `Stainless` — inox poli |
|---|---|---|
| Corps (`…Steel`) | `#858a8f`, spéc. `#595c61`, `_Shininess` 0,30 | `#dbe0e6`, spéc. `#80858c`, `_Shininess` 0,50 |
| Lèvre (`…Edge`) | `#33363b`, spéc. `#595c61`, `_Shininess` 0,55 | `#7a8087`, spéc. `#757a82`, `_Shininess` 0,60 |
| Thème stock | `Gray` | `Silver` |

Ce qui monte sur l'inox, c'est d'abord le **fond** : l'albédo final vaut `texture × _Color`, or la texture partagée est un gris plat à 0,66, ce qui donne ~0,35 en brut contre ~0,58 en poli. La lèvre reste plus sombre que le corps dans les deux livrées, sinon la lame devient une tôle plate de loin.

⚠️ **Le reflet, lui, a dû être rabattu — première version trop forte, vue en jeu le 2026-09-01 : un vrai miroir.** Les deux réglages ne font pas le même travail, et un seul est « à quel point ça brille » :

- **`_SpecColor` = la FORCE du reflet.** C'est le bouton à tourner. Vers le blanc, la surface devient un miroir ; vers la teinte de fond, l'éclat s'éteint. Ramené de `#e6ebf2` (0,92) à `#80858c` (0,52), soit ~1,5× le brut au lieu de ~2,5×.
- **`_Shininess` = la LARGEUR du reflet** (exposant Blinn-Phong, pas une quantité). Haut, il concentre tout dans un point dur — ce qui se lit comme du chrome ; moyen, il l'étale en bande douce, ce qui se lit comme du métal brossé. Ramené de 0,82 à 0,50.
- La **teinte de fond n'a pas bougé** : c'est elle qui était voulue plus claire.

⚠️ **Il n'y a pas de gloss map en secours** : la texture partagée est écrite **sans canal alpha** (vérifié : PNG de type couleur 2, RGB), donc le terme de gloss du shader vaut 1 partout et toute la force du reflet tient dans `_SpecColor`. Ajouter un alpha donnerait un troisième bouton — mais **commun aux deux livrées**, qui échantillonnent la même texture.

Le rendu de contrôle sait maintenant montrer ce réglage (l'entrée `Specular` du Principled suit `_SpecColor`) : il donne le **sens** d'une variation, pas le rendu du jeu — un miroir se serait quand même vu.

**Les deux livrées vivent dans `tools/make_blades.py`** (`LIVERIES`), qui imprime le bloc `ModulePartVariants` complet, prêt à coller, en même temps que les autres lignes de `.cfg`. C'est la même règle que pour la géométrie : la livrée de base est cuite dans le `.mu`, l'autre n'existe qu'en `.cfg`, et **rien ne les confronte au chargement** — un override périmé ne se voit que sur la pièce, dans l'éditeur. Donc on relance le générateur, on ne retape pas des couleurs à la main. Le renderer sait rendre chaque livrée (`--livery Stainless`, ou les deux par défaut), seul moyen de regarder l'inox sans installer.

Le mécanisme et ses pièges (`materialName` apparié en `StartsWith`, `themeName` qui doit exister sous peine d'erreur au chargement, `description` qui est de la config morte, typage des valeurs d'un nœud `TEXTURE`) sont dans le [CLAUDE.md parent](../CLAUDE.md), « Repeindre une pièce sans en ajouter une ». Deux choix propres à ce mod :

- **Thèmes stock plutôt que thèmes maison.** `Gray` et `Silver` existent déjà, ce qui évite l'erreur `Unable to find theme named` **et** fait suivre les lames quand le joueur bascule le thème de tout le vaisseau.
- **Peinture seule.** Ni masse, ni coût, ni quota, ni géométrie ne changent — d'où `useMultipleDragCubes = false`. Une livrée ne doit pas devenir un choix de gameplay déguisé : ce qui sépare les lames, c'est le modèle, pas la couleur.

✅ **Le mécanisme est vérifié en jeu le 2026-09-01** : les deux pastilles apparaissent, la variante s'applique. Seul le **dosage** du reflet a demandé une seconde passe (ci-dessus) ; le rabattement, lui, n'a pas encore été revu en jeu.

**Les trois icônes vont se ressembler**, et c'est structurel : KSP normalise l'icône sur la **plus grande dimension** du modèle — ici la largeur, la seule chose qui sépare les trois lames (mécanisme et leviers dans le [CLAUDE.md parent](../CLAUDE.md), « Les icônes de l'éditeur sont générées au chargement »). Proportions normalisées : 1 : 0,46 : 0,43 / 1 : 0,41 : 0,36 / 1 : 0,37 : 0,30 — trop proches pour se distinguer dans la liste. Si ça gêne à l'usage, il faudra une différence de **forme** (nombre de bras, nervures), pas de taille. Corollaire rassurant au passage : le décentrage de nos modèles n'a **aucune** conséquence sur le cadrage de l'icône.

### Le reste
- ✅ **Fenêtre de debug — faite le 2026-08-31.** Un bouton dans la barre d'app (**vol uniquement** : ce qu'elle commute n'existe qu'en vol), une fenêtre `Bulldozer Debug` avec deux cases à cocher, **Afficher les zones déjà nettoyées** et **Ajouter le bouton de diagnostic dans le PAW** (2026-09-01), et une **combo « Niveau de log »** (2026-09-01, sur le modèle des mods frères — `None`…`Trace`, **`Info` par défaut** comme partout ailleurs). Les trois réglages sont **persistés par installation** dans `PluginData/bulldozer.cfg` ; les deux cases sont décochées par défaut. Sous eux, depuis le 2026-09-02, le **relevé des quads voisins** (ci-dessus, [CLAUDE-code.md](CLAUDE-code.md)) — la seule section de la fenêtre qui ne commute rien : elle lit le monde et se rafraîchit toute seule, donc elle n'a ni réglage à persister ni rien à resynchroniser depuis le view model, et le `ContentBuilder` la parente sans la passer au `ContentController`.

  **La seconde masque l'entrée PAW « Analyser la passe »**, qui traînait dans le PAW de chaque lame d'une partie normale. Même montage que la première, et pour la même raison : un `static` (`BandDiagnostic.ButtonVisible`) que le view model tient en phase avec le réglage persisté, parce que le réglage survit à la scène alors que la lame qui le lit est reconstruite à chaque chargement. La lame le relit dans `OnUpdate`, à côté du libellé de l'aperçu — l'ordre de démarrage entre l'addon de la fenêtre et le `PartModule` n'est pas garanti, et la case peut être cochée pendant que le PAW est ouvert. Rien de plus à faire pour rafraîchir un PAW ouvert : `UIPartActionWindow.CreatePartList(clearFirst: false)` repasse sur ses items à chaque update et retire ceux dont l'event est passé `guiActive = false` (`UIPartActionEventItem.IsItemValid`).

  **Les libellés des niveaux vivent dans `KSP-Shared`** (2026-09-01), clés `logLevel_*` de son `Localization/*.properties`, avec le helper `LogLevels` qui donne la liste des options et le libellé localisé de l'une d'elles. C'est une modif du sous-module, donc à commiter là-bas et à propager ; les mods frères qui offrent ce réglage (SteamInput) ont perdu leur copie des clés et leur `GetLogLevelLabel` au passage — deux jeux de libellés qui divergent, c'est exactement ce que le partage évite.

  ⚠️ **Le niveau de log ne peut PAS être appliqué depuis la fenêtre seule**, et c'est pour ça que `Src/Mod.cs` existe : la fenêtre n'atteint que le vol, alors que `BulldozerScenario.OnLoad` — donc toute l'injection des decals, exactement ce qu'on veut voir en `Debug`/`Trace` — tourne pendant le **chargement** de la scène de vol. Le réglage est donc relu et appliqué par un addon `PSystemSpawn` (une fois par session KSP), la fenêtre ne faisant que le changer en cours de route.

  Quatre choses à retenir de sa mise en place :

  - **Elle a demandé une modif de `KSP-Shared`** (donc à commiter là-bas et à propager) : la zone du bandeau de titre à gauche du bouton « fermer » était **obligatoire** — `PopupBuilder` appelait `_titleBarBuilder.Build()` sans test. Elle est maintenant **facultative** ; sans builder (et avec `MonoBehaviour` en `T`), le bouton de fermeture reste seul contre le bord. Purement additif : les mods qui en fournissent un ne changent pas de comportement. C'est ce qui permet à cette fenêtre de n'afficher que son titre.
  - **Elle a fait entrer la lib uGUI de `KSP-Shared` dans ce mod.** Le `.csproj` ne compilait que trois fichiers partagés ; il prend maintenant `KSP-Shared\Src\**\*.cs`, ce qui a demandé 5 références Unity de plus (`UI`, `UIModule`, `InputModule`, `TextRenderingModule`, `ImageConversionModule`) — et, le 2026-09-02, une de moins : `IMGUIModule` est parti avec le dernier `OnGUI` du mod. Rien à faire côté `build.sh` : il recopiait déjà `KSP-Shared/GameData/Textures/`.
  - **Elle est l'endroit où va un outil de debug**, et pas seulement par rangement : un `KSPAddon` qui définit `OnGUI` paie Unity plusieurs fois par frame, pour toujours (c'est ce qui a rapatrié le relevé de quads ici le 2026-09-02, cf. [CLAUDE-code.md](CLAUDE-code.md)). Hébergée dans la fenêtre, une section ne coûte plus rien fenêtre fermée — `PopupController.Hide()` désactive le GameObject de la fenêtre, donc les `Update` avec. Corollaire : la contrepartie est qu'une fenêtre **laissée ouverte** paie tout ce qu'elle contient, ici un parcours du registre de quads de Parallax 2×/s.
  - **Le vert lit les masques vivants, pas les actions.** C'est le choix structurant (voir `CleanedZonesOverlay`) : les deux sources ne diffèrent qu'aux endroits où quelque chose a échoué silencieusement, et ce sont précisément ceux qu'on veut voir.
  - **La géométrie du tracé rouge a été extraite dans `GroundSheet`** pour que le vert la réutilise. Le refactor a corrigé au passage une vraie limite : la sonde partait d'une altitude unique pour toute la feuille, ce qui ne pouvait pas tenir sur un quad entier — elle part maintenant de la hauteur analytique de chaque point mesuré.

  ✅ **La fenêtre est vue en jeu le 2026-09-02** : elle s'ouvre, et le relevé des quads s'y affiche correctement — mise en page comprise (380 × 360, texte en `Truncate`), ce qui n'était jusque-là que calculé.

  ⚠️ En revanche **ni le vert ni le tracé rouge** n'ont encore été vus : ouvrir la fenêtre ne dit rien de ce que ses cases commutent.

- ✅ **Viser à l'aveugle — fait le 2026-08-31** (`Src/parallax/BandTrace.cs`, même bascule PAW que le surlignage). Le tracé peint les **cases du masque**, pas le rectangle : mêmes règles que l'injection (`ScatterCleaner.RasterFor`), une case n'est peinte que par le quad qui la **possède** (`QuadLocator.Contains`, sinon l'alpha doublerait à chaque couture), et rien n'est peint sur un quad pris par un autre mod. Il suit le relief par **raycast sur le collider** (layer 15) et non par `GetSurfaceHeight` : l'analytique n'est exact qu'aux sommets du quad, à 60 m d'écart sur la Mune, donc un tapis posé dessus s'enterre dans chaque creux. ⚠️ **Pas encore vu en jeu.**
- **Retirer les outils de test** (`Src/diagnostics/`) : `QuadReadout` (et la section de fenêtre qui l'affiche, `Src/ui/ugui/QuadReadout*`) et `BandDiagnostic`. À garder **jusqu'à ce que `bladeTransform` soit validé en jeu** : c'est le premier essai où le cap ne vient plus du point de contrôle, et `BandDiagnostic` est justement ce qui compare `band forward` au navball. `BandDiagnostic` a payé son écot le 2026-08-27 (il a prouvé la visée et chiffré le bug du cap), et c'est le seul moyen de distinguer « le masque est ailleurs » de « la lame passe entre les cailloux ».
- **Détail terrain : exigence caduque.** On voulait imposer le preset maximum, d'abord pour la finesse (elle vient de la texture, pas du maillage : sans objet), puis parce que la taille des quads en dépend. Les deux raisons sont tombées : l'arête est **mesurée en vol** et voyage dans l'action, et un changement de preset est détecté par la garde `maxLevel` (l'action est alors ignorée avec un avertissement, plutôt qu'appliquée de travers). Reste à décider ce qu'on fait de ces actions orphelines — les recalculer à la première occasion en vol serait plus élégant que de les abandonner.
- **Bords de zone** : coupure franche ou dégradé dans le masque (le sampler est bilinéaire, un masque adouci suffirait). Sans objet à 5 m de large — une seule case, aucune place pour un dégradé — et de peu d'intérêt à 20 m : le bord perçu est celui des scatters survivants, déjà irrégulier à l'échelle de leur espacement.

### Questions ouvertes (pas des tâches)
- **Trois familles d'objets au sol, une seule traitée.** Constaté en jeu :
  1. **scatters Parallax** — ceux qu'on nettoie ;
  2. **scatters stock** (`PQSLandControl`, exclus par `removeScatter` → `vbData.allowScatter`) — non traités. Parallax respectant ce flag, un `PQSMod_MapDecal` stock serait la voie complémentaire, sans objet tant que Parallax est installé ;
  3. **surface features de Breaking Ground** — ni des scatters ni du décor : des objets de gameplay persistants, scannables au bras robotique. Vu en plein milieu d'une zone nettoyée, à 7 m du rover. **À ne probablement PAS supprimer** (c'est de la science), même si elles portent un collider et peuvent gêner une grue. À rouvrir seulement si ça devient un problème concret.
- **Effets de bord visuels** : le decal déclare `MeshColorChannel`, d'où la question d'un impact éventuel sur la couleur du terrain et sur les biomes. Rien de visible aux essais.
