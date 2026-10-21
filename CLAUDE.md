# KSP-Mod-Bulldozer — nettoyer une zone de ses rochers (scatters)

> **Ce fichier est LE document du projet** : `README.md` n'est qu'un pointeur vers lui. Un seul document, pour qu'il n'y en ait pas deux à garder en phase.
>
> **Objectif : supprimer les éléments décoratifs posés au sol (rochers Parallax) sur une bande devant le rover, de façon persistante, sans toucher au relief.**
>
> L'objectif d'origine — **niveler** le terrain — est **fermé**, pas reporté : [CLAUDE-archive-nivellement.md](CLAUDE-archive-nivellement.md) dit pourquoi, et garde ce qu'il en reste d'utile. Les connaissances KSP transverses (PQS, internals du jeu, conventions de dev) sont dans le [`CLAUDE.md` parent](../CLAUDE.md) de `kspmod\`.

## Le besoin réel

Je construis mes bases planétaires avec cette technique (validée en jeu, elle marche) :

1. un **Clamp-O-Tron** posé au sol par un ingénieur ;
2. en **construction EVA**, des **poutrelles** assemblées pour former une structure qui **épouse la forme du sol** ;
3. des **ports de docking verticaux** sur cette structure ;
4. une **grue** qui vient docker les modules à la verticale dessus.

Pourquoi cette technique plutôt que « poser des vaisseaux et les docker entre eux » : cette dernière ne survit pas au rechargement. KSP **ne mémorise pas les forces appliquées entre les parties** ; il repositionne la base sans contraintes, une pièce se retrouve dans le sol, la physique démarre, la base est propulsée vers le haut et tout explose. Avec une structure de poutrelles qui suit le relief, la base est déjà à sa place quand la physique reprend → pas d'explosion.

**Le problème aujourd'hui : Parallax.** Le rendu est superbe et je veux le garder, mais les rochers qu'il sème au sol rendent la **construction impossible** : la grue ne peut plus aller où elle veut autour de la base.

D'où le besoin : **pouvoir nettoyer une zone (potentiellement grande) de ses rochers**, une fois, au moment de choisir l'emplacement d'une base.

## Fichiers de contexte — à ouvrir au besoin, PAS par défaut

Ce fichier ne garde que le **pourquoi** du projet. Le détail est réparti ci-dessous ; chaque ligne dit
assez pour savoir **si** le fichier concerne la tâche en cours. Ne pas les lire tous.

- **[CLAUDE-masque-decals.md](CLAUDE-masque-decals.md)** — **le mécanisme**, et le premier à ouvrir
  pour toute question « comment ça marche ». Le PQSMod d'exclusion de Parallax
  (`PQSMod_MapDecalVertexRemoveScatter`), un decal par quad, on n'écrit **que des pixels** ; le repère
  du masque et comment y placer un point ; la résolution (1 px = 5 m, le jeu de 1 m) ; la construction
  d'une passe de lame et la pixelisation ; comment un decal se limite à son quad ; et deux pièges de
  géométrie : **un quad n'a ni la taille ni la forme qu'on croit**, et **les gros scatters sont des
  grappes**, pas des rochers isolés.
- **[CLAUDE-regles-passe.md](CLAUDE-regles-passe.md)** — les règles de gameplay : cas de refus,
  usure de la lame, coût d'une passe, quota, contraintes que le mod s'impose. **À lire avant de
  toucher aux conditions d'acceptation d'une passe.**
- **[CLAUDE-code.md](CLAUDE-code.md)** — la carte des fichiers source (rôle de chacun) et
  l'outillage de diagnostic en jeu. **À lire avant de chercher où se trouve quelque chose.**
- **[CLAUDE-validation.md](CLAUDE-validation.md)** — ce qui est **vérifié en jeu**, avec les mesures
  (Mune, Gilly, Eve), et les pièges déjà payés : `Math.Acos` qui rend NaN sur le quad qui compte,
  `[KSPAssemblyDependency]` pour dériver d'un type Parallax, l'injection hors vol qui casse les
  bâtiments du KSC, les ombres fantômes du surlignage. **À lire avant de re-tester ou de suspecter
  une régression.**
- **[CLAUDE-lames.md](CLAUDE-lames.md)** — les pièces : géométrie des trois lames (BD-2/BD-3/BD-5),
  plusieurs lames sur un même vaisseau, les deux livrées (`ModulePartVariants`), le reste à faire et
  les questions ouvertes. **À lire avant de toucher aux modèles ou aux `.cfg` de pièce.**
- **[CLAUDE-archive-nivellement.md](CLAUDE-archive-nivellement.md)** — l'objectif d'origine, **fermé**
  (pourquoi), et les constats PQS conservés — dont ceux dont le nettoyage de scatters dépend encore
  (KSP reconstruit le PQS à chaque entrée en vol, `PSystemManager` est `DontDestroyOnLoad`).
  **À ouvrir seulement si quelqu'un reparle de niveler le terrain, ou pour l'injection PQS.**

Les connaissances KSP **transverses** (PQS, internals, conventions) sont dans le
[`CLAUDE.md` parent](../CLAUDE.md) de `kspmod\` et ses propres fichiers de contexte.
