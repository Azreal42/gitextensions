# 0133 — Signaler une mise à jour sans interrompre Browse

Statut : implémenté ; [preuves et limites](../../../todo.md#contrôle-hebdomadaire-des-mises-à-jour--périmètre-ciblé).

Advanced reprend les deux choix Windows : recherche hebdomadaire activée, préversions désactivées par défaut. Une publication marquée par GitHub ou par son tag SemVer est une préversion, y compris Linux alpha/bêta. Le dialogue manuel initialise ce choix depuis Advanced ; ses modifications restent locales à la session.

Le démarrage normal, le refresh de la grille/dashboard (dont F5) et Apply des options Updates réadmettent la recherche. Une tentative compte avant HTTP : attendre strictement plus de sept jours évite de solliciter le réseau à chaque démarrage, même après erreur. `UpdateCheckStateStore` garde l’instant UTC dans `XDG_STATE_HOME/gitextensions/update-check.json`, sous verrou coopératif entre processus ; Settings ne peut donc pas l’écraser. État invalide préservé, horloge revenue en arrière encore limitée.

Un résultat attend Main actif, libre et sans modale ; les préférences sont relues avant affichage. L’erreur automatique n’interrompt pas le travail. Le manuel annule l’automatique, conserve son résultat propre et ne modifie pas la fréquence. La fermeture draine les opérations avant de détruire leur client ; aucun téléchargement ni installateur ne se lance sans geste explicite.

Repères : `App` → `MainWindow.EnableAutomaticUpdates`, `MainWindow.Updates/RefreshDataAsync/RefreshDashboardAsync`, `AdvancedSettingsPage/Session/Values`, `UpdateCheckStateStore`, `UpdatesDialog`. Tests : `AutomaticUpdatesTests`, `UpdateCheckStateStoreTests`, `UpdatePreferencesTests` et suites Updates existantes. Helpers, smoke et construction headless ne démarrent pas implicitement une recherche.

N2 conservé. Publication initiale, installation vérifiée par le bureau et audit natif étendu restent ouverts ; les suites et le paquet gardent leurs preuves propres.
