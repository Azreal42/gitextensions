# ADR 0078 · Saisies des scripts

Statut : infrastructure UI N1 testée sur 189c18a6c ; aucun caller de scripts raccordé, aucune famille globale clôturée.

Besoin : reprendre FormQuickStringSelector, FormFilePrompt et SimplePrompt sans perdre la saisie ni confondre sélection et acceptation.

Décision : partager FormQuickItemSelector dans la base Avalonia QuickItemSelectorDialog. Références et chaînes gardent leurs données et leurs règles propres. Le choix de chaînes trie selon CurrentCulture, conserve les doublons, présélectionne le premier élément et garde huit lignes au maximum. Select, Entrée et double-clic valident ; Échap annule explicitement. Un futur caller doit vérifier l’acceptation, même si une valeur reste sélectionnée après annulation ([ADR 0074](../references/0074-quick-reference-selector.md)).

FilePrompt conserve la saisie exacte ; OK n’annule que la chaîne vide. Browse part de « . », choisit plusieurs fichiers existants, remplace le texte par leurs chemins cités et conserve celui-ci si le parcours est annulé. Les gardes de token empêchent une réponse tardive de modifier la fenêtre fermée. OpenFilePickerAsync n’accepte aucun token : BrowseCompletion expose le drainage au futur routeur, sans garantir l’arrêt ni la fermeture du picker natif.

SimplePrompt conserve le défaut non trimé et accepte le vide. Aucun bouton Cancel n’est ajouté. Échap ne s’applique que dans txtUserInput : il désélectionne puis annule. Échap sur OK laisse la fenêtre ouverte.

Repères : QuickStringSelectorDialog, FilePromptDialog, SimplePromptDialog, QuickItemSelectorDialog, ScriptInputDialogControls, ModalWindowActivation ; ScriptInputDialogTests et régressions QuickRef.

Suite : raccorder contexte, demandes typées, préférences, menus et événements, puis valider le transport des arguments et le lanceur ([ADR 0057](../../extensions/scripts/0057-pure-script-preparation.md)). Ces fenêtres ne rendent pas les scripts exécutables. Audit natif du picker, Wayland, DPI et accessibilité restent ouverts. Les 23 cas dédiés sont inclus dans les 1190 tests Desktop exécutés sur la source intégrée ; cela ne valide pas le moteur de scripts.
