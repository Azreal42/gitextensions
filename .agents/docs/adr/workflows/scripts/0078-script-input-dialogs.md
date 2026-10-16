# ADR 0078 · Saisies des scripts

Statut : infrastructure UI N1 et pont de préparation typée ; 25 cas Core et94 Desktop passent dans le clone combiné, dont21 nouveaux cas du pont. Inclus dans Desktop 1241/1241 sur `2e3bba726` ; aucun caller/lanceur raccordé, aucune famille globale clôturée.

Besoin : reprendre FormQuickStringSelector, FormFilePrompt et SimplePrompt sans perdre la saisie ni confondre sélection et acceptation.

Décision : partager FormQuickItemSelector dans la base Avalonia QuickItemSelectorDialog. Références et chaînes gardent leurs données et leurs règles propres. Le choix de chaînes trie selon CurrentCulture, conserve les doublons, présélectionne le premier élément et garde huit lignes au maximum. Select, Entrée et double-clic valident ; Échap annule explicitement. Un futur caller doit vérifier l’acceptation, même si une valeur reste sélectionnée après annulation ([ADR 0074](../references/0074-quick-reference-selector.md)).

FilePrompt conserve la saisie exacte ; OK n’annule que la chaîne vide. Browse part de « . », choisit plusieurs fichiers existants, remplace le texte par leurs chemins cités et conserve celui-ci si le parcours est annulé. Les gardes de token empêchent une réponse tardive de modifier la fenêtre fermée. OpenFilePickerAsync n’accepte aucun token : BrowseCompletion expose le drainage au futur routeur, sans garantir l’arrêt ni la fermeture du picker natif.

SimplePrompt conserve le défaut non trimé et accepte le vide. Aucun bouton Cancel n’est ajouté. Échap ne s’applique que dans txtUserInput : il désélectionne puis annule. Échap sur OK laisse la fenêtre ouverte.

Repères : QuickStringSelectorDialog, FilePromptDialog, SimplePromptDialog, QuickItemSelectorDialog, ScriptInputDialogControls, ModalWindowActivation ; ScriptInputDialogTests et régressions QuickRef.

ScriptPreparationDispatcher fige le contexte et les identités des références avant la première demande. Chaque demande revient au thread UI, y compris après une continuation Core ; la confirmation distingue Yes, No et fermeture. Le nom seul ne suffit pas à identifier une référence : recouper nom/type avec les métadonnées capturées. Fermeture du propriétaire ou token annulent la session ; attendre BrowseCompletion avant de rendre un résultat, sans prétendre fermer le picker natif.

Suite : raccorder le contexte actif et le lanceur, les préférences, menus et événements, puis valider le transport des arguments ([ADR 0057](../../extensions/scripts/0057-pure-script-preparation.md)). Ces fenêtres ne rendent pas les scripts exécutables. Audit natif du picker, Wayland, DPI et accessibilité restent ouverts. Les 23 cas de saisies et 21 cas de préparation sont inclus dans les 1241 tests Desktop de `2e3bba726` ; cela ne rend pas les scripts exécutables.
