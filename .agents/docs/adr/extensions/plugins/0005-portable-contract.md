# ADR 0005 · Contrat plugins portable

Statut : accepté, portage progressif.

Besoin : conserver les outils métiers livrés sans faire dépendre leurs règles de WinForms.

Décision : paramètres et résultats typés, exécution async via le noyau Git ; l’interface native présente les choix, tableaux et confirmations. Conserver les identifiants des plugins. Réemployer les parsers portables originaux.

Create local tracking branches retrouve sa fenêtre compacte : un champ remote initialisé à origin et l’action de création. Cette fenêtre traite toutes les branches du remote ; les scripts gardent la sélection fine du moteur commun. Les branches locales existantes sont préservées, la référence symbolique HEAD du remote est ignorée et aucun checkout n’est effectué. Un succès présente le résumé puis ferme après acquittement ; les erreurs restent accessibles sans masquer les branches déjà créées. Fermer attend la fin de l’opération et de son message possédé.

Conséquence : les DLL tierces liées à Form/Image ne sont pas compatibles par simple copie ; leur contrat et leur UI nécessitent un portage. Les outils non encore portés restent dans TODO.

Repères : `src/linux/GitExtensions.Plugins/PluginContracts.cs`, `src/linux/GitExtensions.Desktop/PluginDialog.cs`, `src/linux/GitExtensions.Desktop/CreateLocalBranchesDialog.cs`, `src/linux/GitExtensions.Plugins/CreateLocalBranchesPlugin.cs`, `src/plugins/`.
