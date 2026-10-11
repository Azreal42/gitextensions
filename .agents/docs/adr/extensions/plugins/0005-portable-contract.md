# ADR 0005 · Contrat plugins portable

Statut : accepté, portage progressif.

Besoin : conserver les outils métiers livrés sans faire dépendre leurs règles de WinForms.

Décision : paramètres et résultats typés, exécution async via le noyau Git ; l’interface native présente les choix, tableaux et confirmations. Conserver les identifiants des plugins. Réemployer les parsers portables originaux.

Conséquence : les DLL tierces liées à Form/Image ne sont pas compatibles par simple copie ; leur contrat et leur UI nécessitent un portage. Les outils non encore portés restent dans TODO.

Repères : `src/linux/GitExtensions.Plugins/PluginContracts.cs`, `src/linux/GitExtensions.Desktop/PluginDialog.cs`, `src/plugins/`.
