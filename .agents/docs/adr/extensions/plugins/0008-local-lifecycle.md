# 0008 · Plugins liés au dépôt

BackgroundFetch s’active à l’ouverture ou au changement de réglages, attend les opérations utilisateur, puis s’arrête au changement de dépôt ou à la fermeture. Éviter les fetchs accumulés quand Git est lent.

AutoCompile propose Build/Skip/Cancel après une mise à jour réussie des sous-modules. Proxy et Gource gardent leurs GUID, icônes et options ; les secrets saisis ne sont pas dupliqués dans les préférences UI.

Statistics et Impact reprennent les calculs et graphiques historiques. Corriger les défauts de parsing sans supprimer de commits du résultat ; les réglages sont conservés via XDG.

Repères : `MainWindow.Plugins`, `BackgroundFetchService`, `AutoCompileSubmodulesService`, `StatisticsPlugin`, `GitImpactPlugin`.
