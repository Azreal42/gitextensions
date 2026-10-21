# 0008 · Plugins liés au dépôt

BackgroundFetch s’active à l’ouverture ou au changement de réglages, attend les opérations utilisateur, puis s’arrête au changement de dépôt ou à la fermeture. Éviter les fetchs accumulés quand Git est lent.

Settings → Plugins → Periodic background fetch expose les cinq réglages Windows : arguments Git, intervalle, rafraîchissement de la vue, fetch des sous-modules et fetch à l’ouverture. L’intervalle 0 désactive la répétition ; le fetch immédiat peut rester une exécution unique. L’avertissement force-with-lease reste en lecture seule et ne fait pas partie des valeurs sauvées.

La page partage les portées XML Local/Distributed/Global, avec Effective en lecture seule et la compatibilité des anciens profils. Ouvrir n’écrit aucun profil ; Apply/OK publient les valeurs enregistrées, Cancel abandonne seulement les brouillons. Le menu du plugin et les scripts ouvrent cette même page. Après sauvegarde, la même instance BackgroundFetchService liée au dépôt est reconfigurée, une fois son ancien travail drainé ; les brouillons ne changent pas le service actif.

Settings → Plugins reprend aussi neuf champs des `GetSettings` Windows :

- AutoCompile : activation automatique, exécutable de compilation et arguments.
- Find large files : seuil de taille des fichiers, initialement 1 MiB.
- Statistics : motifs des fichiers de code, répertoires à ignorer et exclusion des sous-modules.
- Gource : exécutable et arguments (voir ADR 0014).

Ces pages réutilisent les portées XML et les intentions par champ. Seules les modifications explicites sont écrites ; chaque fichier accepté est acquitté avant le suivant, sans effacer les clés étrangères ni migrer les anciens profils à l’ouverture.

AutoCompile partage le même moteur Build/Skip/Cancel entre le menu manuel et le traitement après mise à jour réussie des sous-modules. Enabled conditionne seulement ce dernier : le menu reste utilisable lorsqu’il est désactivé. Apply enregistre les réglages sans lancer de compilation ; annulation et fermeture du propriétaire interrompent et drainent le travail en cours.

Find et Statistics initialisent leurs fenêtres depuis les défauts effectifs XML. Leurs contrôles supplémentaires Linux restent des options temporaires d’invocation ; fermer ces fenêtres ne réécrit ni XML ni JSON. Un Apply dans Settings change les valeurs de la prochaine ouverture. Le seuil fractionnaire est lu selon la langue puis transmis au scanner au format invariant ; une valeur non prise en charge est signalée, sans clamp ni lancement du scanner. Impact conserve son parcours existant.

Repères : `MainWindow.Plugins`, `MainWindow.SimplePluginSettings`, `BackgroundFetchService`, `BackgroundFetchSettingsSession`, `BackgroundFetchSettingsPage`, `SimplePluginSettingsCatalog`, `SimplePluginSettingsSession`, `PluginScopedSettingsSession`, `PluginScopedSettingsPage`, `DesktopSettingsDialog`, `AutoCompileSubmodulesService.BuildAsync` / `AfterSubmoduleUpdateAsync`, `FindLargeFilesDialog`, `StatisticsDialog`, `GitImpactPlugin`.
