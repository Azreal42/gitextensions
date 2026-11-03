# ADR 0159 · Avalonia sur Windows, Linux et macOS

Statut : en cours. Objectif : rendre la même application Avalonia utilisable sur les trois systèmes, avec le comportement et les éléments visuels partagés de Git Extensions. Une compilation ou un smoke test ne prouve pas la parité de tous les workflows.

Décisions :

- Garder Core, Desktop, Plugins et les styles communs. Le nom historique `src/linux` ne limite pas la cible .NET. Propager le RID dans les références de projets : Core transporte un lanceur natif et ne doit pas devenir implicitement indépendant du RID.
- Centraliser les associations fichiers/liens dans `DesktopResourceLauncher` : ShellExecute Windows, `open` macOS, `xdg-open` Linux. Cibles en données, journal de processus existant conservé.
- Respecter la casse native des variables d’environnement. Quoter les chemins d’éditeur pour le shell de Git, y compris Git for Windows ; aucun message ou plan ne devient une commande shell.
- Windows : helper askpass `.exe` autonome, protocole stdout recopié sans transcodage ni diagnostic. macOS/Linux : helper Unix voisin de l’exécutable. Le contrôle DISPLAY reste propre à Linux.
- Livraisons autonomes Windows et bundles `.app` Intel/Apple Silicon via `eng/desktop/`. Vérifier l’architecture, les empreintes de l’archive extraite, le runtime embarqué et le vrai backend graphique sur les runners natifs. Les bundles alpha macOS ont une signature ad hoc ; aucune notarisation n’est annoncée.

Preuves initiales : compilation locale sans avertissement/erreur, 56 tests Desktop ciblés, dont vrais dépôts Unicode/espaces/apostrophes, rebase avec le véritable éditeur déplacé et clic/flèche conservant focus et objets de grille. Une archive Windows PE x64 complète a été construite et contrôlée depuis Linux : ce contrôle statique ne prouve pas son exécution Windows. Références dans `artifacts/cross-platform/20261103/`. Le premier essai de publication a correctement échoué sur un helper Linux transitif ; la propagation explicite du RID corrige ce cas.

Les premiers jobs natifs ont révélé deux défauts corrigés : normalisation des chemins Git for Windows dans les journaux de rebase et sélection de l’ABI Darwin `lstat$INODE64`/`fstat$INODE64` sur Intel. Les 70 contrats passent ensuite sur Windows, macOS Intel et Apple Silicon (run `41650512897`). Windows vérifie aussi son archive et démarre le backend natif. La signature Mac a révélé les notices versionnées placées dans un emplacement de code : elles sont désormais dans `Contents/Resources`, avec vérification récursive après extraction ; cette livraison reste à qualifier sur les runners.

Les jobs `Avalonia native platforms` exécutent les premiers contrats portables, les dialogues askpass headless puis le vrai programme publié. La suite Linux complète reste indépendante. Les suites Windows/macOS complètes, encore liées à des fixtures Unix, restent à porter sans remplacer leurs assertions métier par des tests plus faibles.

La recherche de mises à jour choisit le `.deb` Linux ou l’archive Windows/macOS correspondant à l’architecture native ; elle garde les contrôles des URI, tailles et empreintes. Les 92 tests locaux ciblés passent, dont 13 contrats de sélection et téléchargement portables. Le test de mise à jour via Settings attend maintenant le chargement asynchrone avant son clic réel sur Apply. Le premier run Linux de cette série (`41646245616`) a passé toute la suite et publié son `.deb`.

Écarts techniques encore ouverts : console interactive (`script`, sockets/pidfd Linux), terminaux Windows/macOS et environnement capturé, préservation atomique des journaux `renameat2`, verrous interprocessus macOS, résolution des outils configurés et cycle de vie des descendants hors Linux. Restent également la qualification native des dialogues/menus, raccourcis macOS, haute résolution et parcours d’authentification réels. Les modifications Modal/README/docs préexistantes du primaire ne font pas partie de cette source isolée.

Repères : [CI native](../../../../.github/workflows/avalonia-platforms.yml), [publication](../../../../eng/desktop/package.py), [contrôle livré](../../../../eng/desktop/verify.py), [déploiement macOS Avalonia](https://docs.avaloniaui.net/docs/deployment/macos), [runners natifs GitHub](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
