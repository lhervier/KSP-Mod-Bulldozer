# KSP-BulldozerMod — nivellement de terrain

Mod Kerbal Space Program (KSP 1.12) qui ajoute une **lame de bulldozer** posable sur un rover, pour **niveler / creuser** le terrain autour de lui et préparer une zone constructible. Pas un placeur de structures (≠ Kerbal Konstructs) : on déforme le **vrai** terrain.

> Ce CLAUDE.md conserve l'**architecture retenue** après analyse de faisabilité. Les connaissances KSP transverses (PQS, source décompilée en `d:\ksp-decompiled`, style des commentaires) sont dans le `CLAUDE.md` parent de `kspmod\`.

## État du code

Structure calquée sur KSP-ControlFromHere : projet `.NET 4.7.2`, `KSP-Shared` en **sous-module git** (seuls `Constants.cs` + `ModLogger.cs` sont compilés pour l'instant ; penser à `git submodule update --init` sur un clone neuf), build via `build.bat`/`build.sh` (variables `KSPDIR` / `KSP_DATA_DIR`).

> ✅ **Le flatten FONCTIONNE (confirmé par logs, 2026-06-23).** La « régression » suspectée plus tôt était une **confusion de bouton** + des trous résiduels persistant en mémoire (`DontDestroyOnLoad`, cf. infra). Test propre : disque intérieur de 500 m **plat à `flattenTo` (1378.21)**, mesh ≈ analytique à ~0,1 m près. Les min/max élevés du quad viennent des **coins au-delà de 500 m** (anneau de lissage + terrain naturel), pas du centre.
>
> ✅ **Basculement (« banane ») RÉSOLU — jalon complet validé en jeu (2026-06-23).** Cause racine trouvée : **chronologie**. L'ancien applier (coroutine `FlightGlobals.ready`) aplanissait ~7 s APRÈS que KSP avait déjà assis ET **orienté** le rover sur la **pente** d'origine (via `GoOffRails`→`CheckGroundCollision`). Le sol devenait horizontal sous un rover resté à l'attitude de la pente (~12°) → il tombait sur son bord aval et basculait. **Fix retenu** (cf. `BulldozerTerrainApplier`) : hook `onVesselGoOffRails` (avant le seating) → `skipGroundPositioning = true` (KSP ne réassoit pas sur l'ancien mesh — indispensable car `RebuildSphere` est **async**, le collider plat n'est pas prêt le même frame) → `LevelVessel` aligne l'attitude sur la radiale (sol plat), cap conservé. Vérifié : analytique sous rover = `flattenTo`, rover **droit et de niveau** (`srfRelRotation` revenu dans le registre de la pose stable, à l'opposé de la banane), roues posées à ~0,1 m du plan.
>
> ⏳ **Reste : le « saut » générique des suspensions au rechargement.** Indépendant du mod (arrive sur toute save). C'est ce que **KSP-SaveDeformation** visait sans succès — problème dur et préexistant de KSP. Levier nouveau dont on dispose maintenant : comme on place nous-mêmes le vaisseau (`skipGroundPositioning`), on pourrait tenter de le poser à l'**équilibre de compression** des suspensions plutôt qu'à pleine détente (piste non explorée par SaveDeformation). À traiter comme sous-tâche dédiée, optionnelle.

### `RebuildSphere` était la mauvaise API — utiliser `ResetAndWait` (corrigé 2026-06-23)
Symptôme : aplanissement **intermittent** — parfois nickel, parfois terrain incohérent (quads plats orphelins + analytique restée naturelle + quad sous le rover identique au pristine), y compris au **1ᵉʳ** flatten d'une session fraîche. Cause racine (décompilé) : on appelait `pqsController.RebuildSphere()` = `ResetSphere()` (détruit **inconditionnellement** tous les quads) + `StartSphere(force:false)`. Or `StartSphere(force:false)` **sort en cours de route** si l'état du PQS n'est pas prêt à cet instant : gardes `if (isStarted && !force) return`, `if (scène != jeu) return`, `if (!isAlive) return` (isAlive ⇔ **tous** les mods `OnSphereStart()==true`), `if (target == null) return` (`target` posé par `FlightDriver.SetTarget`). Au moment `onVesselGoOffRails` (chargement du vol) ces conditions ne sont **pas garanties** → quads détruits mais **pas** reconstruits et **`SetupMods` (collecte + `OnSetup` de nos mods) jamais exécuté** → mod inopérant + terrain cassé. C'est une **course** (d'où l'intermittence). **`RebuildSphere` n'est qu'un helper de menu debug** ; KSP en prod utilise `ResetAndWait()` (qui *reset puis attend `isAlive` avant de bâtir*) ou `ForceStart()` (`PSystemSetup`, `PQSMod_CelestialBodyTransform`). **Fix : `TerrainLeveler` appelle désormais `ResetAndWait()`** (async, OK car le seating ne dépend pas d'un mesh same-frame grâce à `skipGroundPositioning` + `LevelVessel`). NB : `ResetAndWait`/`ForceStart` ne s'exécutent que si `parentSphere == null` (vrai pour le PQS principal d'un corps). ⚠️ **À valider en jeu** (intermittence supprimée ?).

### Outil de diagnostic (read-only)
`Src/diagnostics/BulldozerDiagnosticPlugin.cs` — `[KSPAddon(Flight)]`, **2ᵉ bouton toolbar à icône grise « vide »** (générée en code, distincte du bouton flatten). Au clic, **ne modifie rien** : dump dans `KSP.log` de (a) la pose vaisseau .sfs + dérivés de seating (`lat/lon/alt/hgt`, `heightFromTerrain`, point bas collider/roues vs `GetSurfaceHeight`), et (b) pour le quad-feuille sous le rover + 4 voisins, chaque sommet du mesh avec son altitude **mesh** vs **analytique** et leur `delta`. Sert à comprendre le lien pose↔PQS et à mesurer l'écart mesh/analytique.

**Constats du premier dump (Kerbin, 2026-06-23, détail terrain = MAX) :**
- **`maxLevel=10`, quad sous le rover `sub=10`, espacement ~63 m** (arête de quad ~870 m, grille 15×15). ⇒ **au réglage max, le grain Kerbin est ~63 m**, plus fin que les ~100 m de la table « sub=9 » (mesurée à un détail inférieur). La table de grain par corps doit être relue à la lumière du réglage de détail actif.
- Sur terrain **naturel**, `meshAlt ≈ analyticAlt` à ±0,07 m (bruit d'interpolation) : le mesh est un échantillonnage fidèle de l'analytique aux sommets, linéaire entre eux. L'outil lit donc bien les deux.
- Seating sain : point bas collider ≈ 0,26 m sous la surface analytique (roues légèrement comprimées), `heightFromTerrain == radarAltitude`.

**Implémenté = jalon save+reload, et c'est KSP qui repose le rover** (le checkpoint live a été abandonné, cf. infra) :
- `Src/persistence/BulldozerAction.cs` — une action `{ body, lat, lon, radius, flattenTo }` (+ load/save ConfigNode).
- `Src/persistence/BulldozerScenario.cs` — `ScenarioModule` (`AddToAllGames`, FLIGHT/SPACECENTER/TRACKSTATION) : **source de vérité** par save. `OnLoad`/`OnSave` (dé)sérialisent les actions ; `AddAction` enregistre. (N'applique PAS le flatten — voir ci-dessous.)
- `Src/BulldozerPlugin.cs` — `[KSPAddon(Flight)]`, bouton toolbar. Au clic : enregistre une action (cible = `TerrainAltitude` sous le rover, rayon 500 m) → `SaveGame` (save **dédiée** `"BulldozerReload"`, pas `"persistent"`) → `FlightDriver.StartAndFocusVessel`. **Pas de nivellement manuel, pas de patch `.sfs`, pas de repositionnement.** (Essayé puis retiré : cible « point bas du footprint » — sur une grue le bras décalait le min ailleurs → dalle au mauvais niveau.)
- `Src/BulldozerTerrainApplier.cs` — `[KSPAddon(Flight)]`. **Refondu 2026-06-23** : ne tourne plus dans une coroutine `FlightGlobals.ready` (trop tard, après le seating → basculement). S'accroche à **`GameEvents.onVesselGoOffRails`** (qui tombe **avant** `CheckGroundCollision`, dans le même `GoOffRails`). Pour le vaisseau actif, une seule fois : (1) `ApplyActions` (**purge toujours**, même avec 0 action) ; (2) si une action concerne ce corps, `vessel.skipGroundPositioning = true` (KSP ne réassoit pas sur l'ancien mesh — nécessaire car `RebuildSphere` est **async**) ; (3) `LevelVessel` met le rover **de niveau** (up aligné sur la radiale = normale du sol plat), cap conservé. ⚠️ **À valider en jeu** (timing de la reconstruction async).
- `Src/terrain/TerrainLeveler.cs` — `ApplyActions` = **purge** tous nos `BulldozerFlatten` de tous les corps (anti-fuite inter-saves) → **replay** (un `PQSMod_FlattenArea` par action) → **`RebuildSphere`** des corps touchés.

**Repositionner le rover = cul-de-sac (abandonné, voir l'historique des explosions).** Tout repositionnement **en vol** d'un vaisseau en physique active le **disloque** : `CheckGroundCollision()` direct → explosion ; `GoOnRails`/`GoOffRails` (le vaisseau **actif** ne se packe pas vraiment) → explosion aussi. La voie « recharger avec pose exacte + `skipGroundPositioning=True` » (façon ReRoot, physiquement sûre) bute sur le **calcul de l'altitude du point bas** : AABB des colliders d'une grue plonge ~10 m sous les roues, `TerrainAltitude(centre)` enterre la roue aval. Constat : **KSP ne nivelle PAS un vaisseau posé au chargement** (il réapparaît à sa rotation puis se tasse). 

**Décision pragmatique actuelle (état retenu) :** on ne repositionne plus du tout. On aplanit à `TerrainAltitude` sous le rover ; le vaisseau est chargé à sa pose sauvegardée, le terrain descend à ce niveau, et le rover **apparaît un peu en l'air puis tombe/se tasse** sur la zone plate tout seul. Simple, pas d'explosion. Le nivellement parfait « zéro mouvement » (pose exacte calculée + `skipGroundPositioning`, altitude via la vraie logique roues de KSP type `getLowestPoint`) est **différé** comme sous-tâche dédiée. **Pas encore fait** : zone ronde paramétrable / hauteur partagée entre passes (grandes surfaces cohérentes) / filtre dénivelé / creusage / PQSMod custom à fold / pièce-lame / conso élec.

### Résultat du checkpoint — validé en jeu (Kerbin), avec une contrainte majeure

Le **mécanisme fonctionne** : injecter un `PQSMod_FlattenArea` enfant de `body.pqsController` + `RebuildSphere()` aplanit bien le terrain (probe analytique **et** rendu confirmés en jeu).

**MAIS contrainte découverte (déterminante pour tout le projet) :** à 10 m → aucun effet, à 50 m → aucun effet, à **500 m → visible**. La déformation ne peut déplacer que les **sommets existants** du maillage PQS au niveau de subdivision max. Espacement **mesuré en jeu** (quad-feuille sous le rover, scan de `PQ.verts`) :

> Kerbin : quad-feuille `sub=9`, grille **15×15** sommets, arête de quad **~1405 m** → **espacement ~100 m** entre sommets voisins.

Un disque < ~100 m ne contient donc aucun sommet → le maillage ne change pas (même si la hauteur *analytique*, via `GetSurfaceHeight`, change). **Le « niveler 10 m autour du rover » est IMPOSSIBLE à la résolution PQS stock sur Kerbin** : le grain minimal est ~100 m, et un disque *reconnaissable* demande plusieurs espacements (~300–500 m). Le gameplay « lame fine + nombreux aller-retours » tombe.

Grain **mesuré sur 4 corps** (clic unique, rover posé) — il dépend du `maxLevel` du corps, **PAS de sa taille** :

| Corps | maxLevel | Espacement |
|---|---|---|
| Mun | 8 | ~64 m (le plus fin) |
| Kerbin | 9 | ~100 m |
| Minmus | 6 | ~114 m |
| Eve | 9 | ~190 m (le plus grossier) |

Minmus (petit) n'est PAS le plus fin (sub 6). **Aucun corps n'approche 10 m** ; le plus fin est la Mun à ~64 m. → Le « niveler 10 m » est définitivement hors stock : **c'est un outil de grandes zones**. **Décision design (quasi forcée par la donnée) :** (A) accepter le grain stock → terraformation de **grandes zones** (une passe ≈ déjà une plateforme) ; (B) augmenter `maxLevel`/subdivision PQS pour viser ~10 m → gros chantier hors-stock, coûteux, incertain ; (C) colliders plats superposés (rejeté : plus du vrai nivellement).

### Validé en jeu sur Kerbin / Mun / Minmus / Eve

Le mécanisme **fonctionne et est correctement centré** sur tous les corps testés : `probeAfter == target` et `rover<->modCenter angle = 0.0°, surfOffset = 0.0 m` partout (le centrage via `useLatLon` est bon — `GetSurfaceNVector(identité)` == `GetRelSurfaceNVector`).

**Le rebuild EN DIRECT est fondamentalement fragile** (cul-de-sac). Symptômes observés, tous dus au fait de reconstruire un PQS vivant que d'autres systèmes manipulent en même temps :

1. **Dôme intermittent** : le quad **sous le vaisseau** n'est parfois pas reconstruit (le vaisseau « tient » sa tuile) → l'ancien maillage reste en bosse.
2. **Rover éjecté en l'air** : quand le collider de la tuile est reconstruit sous lui, Unity le voit en intersection et l'éjecte.
3. **Non-application intermittente** : parfois le flatten (pourtant bien setup : `OnSetup` tourné, centré 0°, `enabled`, `order=1000`) n'affecte plus la hauteur, même à son propre centre (`probeAtModCenter` == sol) — PQS dans un état corrompu.
4. **Fissures / trous aux jointures de LOD** (vu sur Eve) : un quad fin déformé, le voisin grossier ne suit pas sur l'arête → trou.
5. **Corruption via la triche** : un 2ᵉ « Set Position » déclenche des reconstructions PQS qui, avec nos mods persistants, cassent le terrain (plans incohérents, sol très sous le datum).

**Persistance involontaire (`DontDestroyOnLoad`)** : `PSystemManager` est `DontDestroyOnLoad` (`PSystemManager.cs:129`) → les corps + leur `pqsController` vivent toute la **session KSP** et ne sont PAS reconstruits au chargement d'une save. Conséquences de notre injection runtime :
- les déformations **persistent au rechargement** d'une save (le mod reste accroché au corps en mémoire) ;
- mais elles **ne sont PAS dans la save** → perdues au **redémarrage** de KSP ;
- et elles **fuient entre parties** (corps partagé en mémoire) : charger une autre save dans la même session garde la déformation. ⇒ ce n'est PAS de la vraie persistance, c'est un effet de bord global-session.

**Conclusion : abandonner le rebuild en direct.** Le mécanisme de fond (injecter un `PQSMod_FlattenArea` + reconstruire) est prouvé (aplanissement propre et centré sur 4 corps) ; seule la *méthode d'application live* est mauvaise. Le jalon **save+reload** la remplace et supprime structurellement 1→5 (reconstruction à froid, sans vaisseau en physique) ; il doit en plus **purger nos mods** au chargement puis **rejouer** uniquement les actions du `ScenarioModule` de la save courante (corrige la fuite inter-saves + le redémarrage).

**Purge-au-chargement (clé contre la fuite inter-saves)** : la source de vérité est le `ScenarioModule` de la save, JAMAIS le corps en mémoire (qui persiste via `DontDestroyOnLoad`). À chaque `OnLoad` : (1) retirer *tous* les GameObjects `BulldozerFlatten` de *tous* les corps → corps vierge, (2) rejouer seulement les actions de cette save. Impératifs : le scénario doit être **enregistré pour tous les modes de jeu** (`[KSPScenario(... AllGameModes ...)]`) pour que la purge tourne **même en chargeant une save sans le mod** (sinon une déformation en mémoire d'une partie précédente survivrait, faute de quoi pour la nettoyer) ; la purge tombe pendant l'écran de chargement (vaisseau non physique) donc invisible et sans éjection.

(Le rover qui tombait/explosait en creusant venait du `DebugDigDepth` — remis à 0.)

### Spec : exiger le niveau de détail du terrain au maximum

Le grain (~100 m sur Kerbin) dépend du réglage KSP **« niveau de détail du terrain »** (Settings → Graphics), qui choisit un *preset* PQS (donc `maxSubdivision`). Le baisser aggrave encore le grain, et — surtout — change le terrain de base, ce qui casserait la **cohérence du rejeu d'actions** (event-sourcing : mêmes actions rejouées sur un terrain de base différent ⇒ résultat différent).

**Spec (à coder plus tard, pas maintenant) : le mod doit refuser de démarrer / désactiver son action si ce réglage n'est pas au maximum** (log clair + bouton grisé), pour garantir un grain minimal connu et la reproductibilité de la persistance.

Valeur **lisible par code** (vérifié dans le décompilé) : `PQSCache.PresetList` — champ **`public static`** de type `PQSGlobalPresetList` — expose :
- `presetIndex` (int) : index du preset de détail actif ;
- `preset` (string) : son nom ;
- `presets` (`List<PQSPreset>`) : tous les presets disponibles, ordonnés du moins au plus détaillé (Low / Default / High…).

Test « au max » ⇔ `PQSCache.PresetList.presetIndex == PQSCache.PresetList.presets.Count - 1`. Variante robuste si l'ordre n'est pas garanti : prendre le preset dont les `spherePresets[].maxSubdivision` sont les plus élevés et vérifier que c'est l'actif.

**Piège de diagnostic à retenir :** pour sonder la hauteur après flatten, il FAUT l'overload **interne** `PQS.GetSurfaceHeight(Vector3d, bool)` avec `true` (via réflexion). L'overload public passe `false` → le `FlattenArea` sort sur sa barrière `quadActive` (jamais positionnée hors d'un vrai build de quad) et renvoie la hauteur d'origine → probe faussé (faux négatif).

**Code de debug à retirer une fois la résolution tranchée :** `DebugDigDepth` (creuse au lieu d'aplanir à plat) dans `TerrainLeveler`, et `FlattenRadius = 500` dans `BulldozerPlugin`.

## Gameplay retenu

- Une pièce-lame (PartModule) greffée sur un rover.
- Une action (clic droit) **surligne une zone ronde** autour du rover, **collée aux sommets du terrain**, couvrant les sommets dans un **rayon choisi par l'utilisateur**, et **dont le dénivelé par rapport au plan cible est < hauteur max** (en + comme en −). Zone ronde retenue parce que c'est exactement ce que fait le stock (voir « Le stock fait pareil »).
- À la validation : on applique la déformation, puis **rechargement** → le rover réapparaît au même endroit, terrain nivelé autour.
- Mode **creuser** : même mécanisme, plan cible sous la hauteur courante.
- **Anneau de lissage** autour du disque plat (revient sur la décision initiale « bords francs ») : un bord franc crée une face quasi-verticale mono-face, back-cullée vue d'en bas → on voit **à travers** jusqu'à l'océan, et la frontière entre deux nivellements voisins fait un « mur » invisible. L'anneau (`innerRadius`=rayon plat, `outerRadius`=rayon+`RingWidth` 300 m, `smoothStart/End`=0) rampe doucement vers la hauteur accumulée → pas de face verticale, et lisse aussi la jonction entre deux flattens (le `CubicHermite` du stock mélange vers `vbData.vertHeight`, donc vers H_A là où un flatten précédent a déjà agi).
- Idée annexe : forte conso électrique pendant le nivellement.

## Pourquoi c'est faisable (et pourquoi la version « temps réel » ne l'est pas)

Le terrain KSP est un **PQS** (Procedural Quadtree Sphere) : la hauteur d'un sommet n'est **pas stockée**, elle est **recalculée** à chaque construction de quad par la somme des **PQSMods** (`PQS.BuildQuad` → `Mod_OnVertexBuildHeight`). Surtout, la hauteur « logique » qu'utilise tout le jeu vient de `PQS.GetSurfaceHeight`, qui **rejoue le même calcul analytique**, indépendamment du maillage affiché.

Conséquences vérifiées dans `d:\ksp-decompiled` :

- **Éditer le `mesh` d'un quad est purement cosmétique** : `GetSurfaceHeight` ignore le mesh, donc tout se désynchronise (état posé, altitude, autres vaisseaux) et l'édition disparaît au prochain rebuild du quad. → **Il faut modifier les PQSMods, pas le maillage.**
- Le collider physique = le mesh du quad (`PQSMod_QuadMeshColliders.OnQuadBuilt` : `meshCollider.sharedMesh = quad.mesh`), uniquement sur les quads de subdivision max.
- La déformation **temps réel** (lame qui aplanit en roulant) est rejetée : elle exigerait un rebuild ciblé des quads à chaque frame → coûteux et fragile. Le modèle **acte discret + rechargement** l'évite : c'est le rechargement qui régénère les quads (comme le font les flatten stock `PQSMod_FlattenArea` / `MapDecal`, appliqués au chargement).

API utile (vérifiée) : `PQS.quads` (`PQ[]`) + `PQS.normalUpdateList` pour énumérer les quads actifs ; `PQ.verts` (`Vector3[]`) + `PQ.quadMatrix` pour passer les sommets en monde ; `PQ.subdivision` / `meshVertMin/Max`. Un vaisseau posé stocke `Vessel.latitude/longitude/altitude` + `Landed`.

## Le stock fait pareil : `PQSMod_FlattenArea` (mémorial Armstrong sur la Mun)

La zone plate autour du mémorial Niel Armstrong est aplanie par un **`PQSMod_FlattenArea`** (le `PQSCity` voisin ne fait que poser l'objet + gérer le LOD, il ne touche **pas** la hauteur). Les configs des corps stock ne sont pas en texte (construites en code/assets binaires), mais la **mécanique** de la classe est exactement la nôtre. Logique débruitée :

```csharp
// Court-circuit spatial au niveau quad (OnQuadPreBuild) :
testAngle = Acos(dot(quad.positionPlanetRelative, posNorm)); // distance angulaire au centre
quadActive = testAngle < angleQuadInclusion;                 // quad hors zone → désactivé

// Aplanissement par sommet (OnVertexBuildHeight) :
if (!quadActive) return;                          // saute tout le quad si loin
testAngle = Acos(dot(vbData.directionFromCenter, posNorm));
if (testAngle >= angleOuter) return;              // au-delà de l'anneau externe : intact
if (testAngle < angleInner) {                     // zone interne : aplanissement franc
    vbData.vertHeight = flattenToRadius;          // ← hauteur cible ABSOLUE
    return;
}
// anneau intermédiaire : lissage CubicHermite (le falloff)
aDelta = (testAngle - angleInner) / angleDelta;
vbData.vertHeight = CubicHermite(flattenToRadius, vbData.vertHeight, smoothStart, smoothEnd, aDelta);
```

Ça valide notre design point par point : hook `OnVertexBuildHeight` (jamais le mesh) ; court-circuit spatial **angulaire** au niveau quad (plus élégant qu'une boîte englobante sur une sphère) ; **cible absolue** (`flattenToRadius`, calculée une fois dans `OnSetup`) ; bords francs = brancher `angleInner = angleOuter` (pas d'anneau Hermite). Pour une zone **ronde**, FlattenArea est carrément le modèle direct : rayon en mètres → angle = `rayon / body.Radius`.

Notre PQSMod custom ≈ **une pile de `FlattenArea` rejouées dans l'ordre**. Ce qu'on **ajoute** au stock : le filtre d'éligibilité « delta < max » (le stock aplanit *tout* dans le rayon, quelle que soit la pente) et le **fold de N actions** au lieu d'une seule.

## Architecture technique retenue

### 1. PQSMod custom additif
Un PQSMod maison injecté dans `body.pqsController.mods`. Dans `OnVertexBuildHeight`, pour chaque sommet : hauteur de base, puis **repli (fold) des actions dans l'ordre**. Une action = « **mettre** (valeur absolue, pas offset) les sommets éligibles de la zone à la hauteur cible » → sur chevauchement, la dernière action gagne, sans accumulation parasite.

### 2. Persistance par event-sourcing (PAS de hauteurs stockées)
On ne mémorise **pas** la hauteur de chaque sommet (ferait exploser les saves) mais la **liste ordonnée des actions**, rejouée au chargement. Un `ScenarioModule` sérialise les enregistrements :

```
{ body, lat, lon, rayon, hauteurCible, mode(aplanir|creuser)[, maxDelta] }
```
(zone ronde uniquement → pas de champ « forme » : le rayon suffit.)

Règles de déterminisme du rejeu :

- **Stocker la hauteur cible RÉSOLUE en absolu** (altitude), calculée au moment de l'action — surtout pas « le niveau du rover » recalculé au chargement (le rover n'est plus là).
- **Ordre conservé** : `terrain = base ∘ action₁ ∘ action₂ …`. La base PQS est déterministe (même version KSP + même corps).
- L'**éligibilité** (`delta < max`) se **recalcule** au rejeu contre le terrain-déjà-replié → cohérent par récurrence (chaque action revoit le même terrain qu'au moment réel).
- **Limite assumée** : si la version de KSP ou un mod de planètes change le terrain de base, le rejeu se décale (les plateaux restent à la bonne altitude absolue ; seule l'éligibilité des bords peut bouger). Aucun souci en jeu normal.

### 3. Performance — le seul vrai point délicat
Le PQSMod tourne pour **chaque sommet de chaque quad** sur **toute la planète**. Imposer un **court-circuit spatial** dès le départ :

- Niveau quad : si la boîte englobante ne croise **aucune** zone d'action → tout sauter (99,9 % de la planète).
- Sinon, ne replier que les actions dont la zone couvre le sommet.

Index spatial même simple (les actions sont groupées autour des bases). Reste négligeable même à des centaines d'actions.

## Décisions de design à trancher (non figées)

- **Plan cible du nivellement** : niveau de contact des roues du rover recommandé (le rover ne « saute » pas — le déplacement vertical se fait sur les bords, ~0 sous le rover). En creusage, le rover descend de la profondeur = comportement attendu.
- **Ré-assoiement du rover** : quand le sol change sous un vaisseau posé, il retombe sur la nouvelle surface (rechargement *ou* rebuild en place). Inhérent.
- **Rechargement vs `pqsController.RebuildSphere()`** : le rechargement est le plus robuste (zéro désync) ; `RebuildSphere` évite l'écran de chargement mais est plus subtil à fiabiliser.

## Jalon minimal de validation (cadré)

But : prouver toute la chaîne **PQSMod → persistance → rebuild → cohérence logique** avec le minimum de code. Périmètre figé : **pas de choix de rayon** (10 m en dur), nivellement **au niveau du sol sous le rover**, **zone ronde**. Simplification clé : pour **une seule action**, on **réutilise le `PQSMod_FlattenArea` stock** — le PQSMod custom additif (fold de N actions) ne vient qu'après.

### Périmètre du mod-embryon
- Un bouton dans la barre d'application (`ApplicationLauncher`).
- Au clic : capturer la position du rover et la cible, enregistrer l'action, sauver + recharger.
- Au rechargement : le terrain est aplani dans un rayon de 10 m autour du rover, au niveau du sol qui était sous lui.

### Flux au clic (avant recharge)
1. Capturer `vessel.latitude`, `vessel.longitude`, et la **cible** = altitude du terrain sous le rover (`vessel.mainBody.TerrainAltitude(lat, lon)`).
2. Enregistrer l'action `{ bodyName, lat, lon, flattenTo }` dans un **`ScenarioModule`** (ce qui survit à la sauvegarde et teste réellement la persistance).
3. Sauver + recharger via le pattern de **KSP-VesselBookmark** (`Src/util/VesselNavigator.cs`, branche save-and-reload) :
   ```csharp
   var game = HighLogic.CurrentGame.Updated(GameScenes.FLIGHT);
   GamePersistence.SaveGame(game, "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
   FlightDriver.StartAndFocusVessel("persistent", FlightGlobals.Vessels.IndexOf(vessel));
   ```

### Flux au chargement (le cœur du jalon)
Le `ScenarioModule` relit l'action et, une fois le PQS du corps prêt (s'accrocher à `GameEvents.onFlightReady` ou au moment où `body.pqsController` est actif), construit un `PQSMod_FlattenArea` stock et l'injecte :
```csharp
flatten.useLatLon = true; flatten.lat = ...; flatten.lon = ...; flatten.alt = ...;
flatten.flattenTo = flattenTo;                          // OnSetup → flattenToRadius = sphere.radius + flattenTo
flatten.innerRadius = 9.9; flatten.outerRadius = 10.0;  // ≈ bords francs ; inner < outer pour éviter angleDelta = 0
flatten.OnSetup();                                      // recalcule angles + flattenToRadius
body.pqsController.RebuildSphere();                      // terrain ET GetSurfaceHeight cohérents
```
Rappel : un PQSMod ajouté à l'exécution n'est **pas** sérialisé par le stock → c'est notre `ScenarioModule` qui persiste l'action et reconstruit le FlattenArea à chaque chargement.

### Critère de succès
Pas seulement « ça a l'air plat ». Vérifier qu'après recharge **`body.pqsController.GetSurfaceHeight()` au point du rover renvoie bien `flattenTo`** (le terrain *logique* a bougé, pas que le visuel) **et** que ça **persiste** à une 2ᵉ recharge. Si ces deux points passent, le reste (zone ronde, filtre de dénivelé, creusage, conso élec) n'est que de la déclinaison.

### Checkpoint intermédiaire recommandé (dérisque l'inconnu)
Avant de brancher la persistance/recharge, faire une version **en direct** : au clic, injecter le `PQSMod_FlattenArea` + `RebuildSphere()` **sans recharger ni rien persister**. Comportement attendu de cet embryon :
- Clic → le terrain s'aplanit au niveau du sol sous le rover, et en roulant le rover **suit bien** le nouveau terrain (le collider = le mesh du quad, reconstruit lui aussi).
- **Mais au rechargement, la déformation a disparu** (le FlattenArea live n'est pas sérialisé) : le PQS revient au terrain d'origine. Si entre-temps on a roulé sur une zone qui était à l'origine **plus haute** que le plan nivelé (une bosse arasée), le rover se retrouve **encastré dans le sol d'origine** et explosera probablement ; sur une zone qqui était un **creux comblé**, il retombe à la place. C'est précisément ce que la persistance (`ScenarioModule` + reconstruction du FlattenArea) viendra corriger.

Ce checkpoint isole le risque « PQS » du risque « persistance ». Une fois vert, on ajoute le `ScenarioModule` + la recharge → jalon complet.

### Hors-périmètre connus
- **Rebond des suspensions** au rechargement : on avait tenté un mod frère **KSP-SaveDeformation** (censé préserver la compression des `ModuleWheelSuspension`) mais **il n'a jamais fonctionné** — ne pas compter dessus. De toute façon non nécessaire ici tant qu'on nivelle au sol sous le rover (déplacement net ≈ 0).
- Choix de rayon, forme, creusage, conso électrique, fold de N actions, PQSMod custom : déclinaisons post-jalon.
