# Archive : le nivellement du terrain (objectif fermé)

Pourquoi le nivellement est abandonné — décision ferme, pas un report — et les constats vérifiés qu'il coûterait cher à ré-établir.

> Fichier de contexte de **KSP-BulldozerMod**. Le fil directeur du projet est dans [CLAUDE.md](CLAUDE.md).

## Pourquoi le nivellement est abandonné

Décision ferme, pas un report. Deux constats indépendants :

- **Le grain PQS interdit le gameplay visé.** La déformation ne peut déplacer que les sommets existants du maillage au niveau de subdivision max. Mesuré en jeu : Mun ~64 m, Kerbin ~100 m, Minmus ~114 m, Eve ~190 m entre deux sommets. « Niveler 10 m autour du rover » est **hors de portée du stock** ; on ne peut faire que de la terraformation de très grandes zones. *(À ne pas transposer au nouvel objectif : le nettoyage de scatters passe par une texture masque échantillonnée par triangle, il n'est donc pas limité par ce grain — voir [CLAUDE-masque-decals.md](CLAUDE-masque-decals.md).)*
- **Les effets de bord sont inhérents, pas des bugs de mon code.** Trous/fissures dans la map aux jonctions de LOD, vaisseaux qui explosent en réapparaissant dans la zone. **Vérifié avec Kerbal Konstructs**, qui pose aussi des zones plates : **mêmes effets de bord**. Ce n'est donc pas une question d'implémentation.

À quoi s'ajoute que, ma technique de poutrelles épousant le sol fonctionnant très bien, **je n'ai plus besoin de terrain plat du tout**. Seuls les rochers me gênent.



## Archive : l'objectif abandonné (nivellement du terrain)

> **Plus aucun de ces fichiers n'existe** (`BulldozerFlattenMod`, `TerrainLeveler`, `BulldozerTerrainApplier`, `BulldozerVesselSeater`, `BulldozerPlugin`, `BulldozerDiagnosticPlugin`) — tout est récupérable dans git. Ne restent ici que les **constats vérifiés** qui coûteraient cher à re-établir, en une ligne chacun. La raison de la fermeture est en [CLAUDE-archive-nivellement.md](CLAUDE-archive-nivellement.md).

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
