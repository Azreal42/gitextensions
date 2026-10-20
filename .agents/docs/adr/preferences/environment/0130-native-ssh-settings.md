# ADR 0130 — Choisir le client SSH sans remplacer la configuration native

Reprendre le choix SSH Windows, sous Git Extensions avec l’icône Key et les contrôles communs. Le mode natif conserve la session héritée ; un autre client est un exécutable littéral, sans arguments ni quoting shell, publié comme GIT_SSH dans les seuls enfants. Git garde l’autorité : GIT_SSH_COMMAND, core.sshCommand effectif, GIT_SSH puis PATH. Variantes, agent et askpass natifs restent prioritaires.

SSH et Paths partagent le profil et la session GitEnvironment. Apply/OK publient leurs brouillons ensemble ; Reload conserve les intentions modifiées et les champs inconnus. La case de démarrage ne publie jamais le SSH en attente. Les opérations commencées, scripts et helpers gardent leur contexte ; les nouvelles commandes des dépôts ouverts prennent le choix publié.

Le diagnostic distingue le binaire testé par -V de la commande prioritaire de Git. Une commande shell configurée n’est pas exécutée pour cette sonde ; son autorité est visible en Warning, sans prétendre valider une authentification. Une édition/publication invalide l’ancienne observation. Repair retrouve la même page. Browse reste possédé jusqu’au retour effectif du picker natif ; Cancel invalide le résultat tardif sans promettre un arrêt que StorageProvider ne fournit pas.

Plink, PuTTYgen et Pageant existent sous Unix. Restent un adaptateur d’agent possédé (socket, durée de vie, chargement des clés des remotes), l’action CLI PuTTYgen et l’interactivité Plink. Le premier lot consommé ne clôt pas ces fonctions ; aucun champ inerte pour les simuler.

Repères : SshSettingsPage, GitEnvironmentSettingsSession/PreferencesStore, GitExecutionEnvironment, GitEnvironmentDiagnostics, GitConfigurationContext, MainWindow.ToolsHelp. Tests : SshEnvironmentScenarios, SshSettingsPageTests/SessionTests et suites GitEnvironment existantes. Source c358d990f, ciblé et natif source EN/clair + FR/sombre : [preuves et limites](../../../todo.md#ssh--perimetre-verifie), portage N2.
