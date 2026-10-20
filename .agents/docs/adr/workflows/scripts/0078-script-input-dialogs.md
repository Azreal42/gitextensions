# ADR 0078 · Saisies des scripts

Statut : pont UI consommé par le runtime, suites complètes de la source `19bbfed70` acceptées ; paquet exact vérifié ; natif S01 du runtime ciblé. Les trois saisies conservent leurs niveaux N1 et leur audit natif ouvert dans cette référence. Cette référence reste historique ; les compléments qualifiés de l’[ADR0126](0126-generic-prompts.md) portent QuickStringSelector et SimplePrompt à N2 sur leurs parcours testés. FilePrompt reste N1.

Reprendre FormQuickStringSelector, FormFilePrompt et SimplePrompt sans perdre la saisie ni confondre sélection et acceptation.

ScriptHostContext et ScriptPreparationDispatcher figent les faits avant la première demande, puis reviennent au thread UI pour chaque prompt d’une unique préparation. Confirmation distingue Yes, No et fermeture. Les références sont recoupées par nom/type avec les métadonnées capturées ; fermer un sélecteur ne valide jamais sa présélection. Le contrat historique peut convertir un choix de référence/remote fermé en valeur vide ; cela reste distinct d’un UserInput annulé.

QuickStringSelector conserve tri CurrentCulture, doublons, première sélection et huit lignes au maximum. Select, Entrée et double-clic valident explicitement. SimplePrompt garde titre/libellé/défaut littéraux et accepte le vide, sans bouton Cancel ajouté. Échap appartient à txtUserInput : désélection puis annulation ; Échap depuis OK laisse ouvert. Les jetons UserInput avec espaces terminaux sont consommés littéralement.

FilePrompt conserve la saisie exacte ; seule la chaîne vide est annulée par OK. Browse part de « . », choisit plusieurs fichiers locaux existants, remplace la chaîne par les chemins cités et conserve le texte si Browse est annulé. Les réponses tardives ne publient pas dans un propriétaire fermé. L’API du picker ne prend pas de token : attendre BrowseCompletion garantit le drainage connu, pas l’arrêt ni la fermeture du picker natif.

DesktopScriptExecutor consomme le résultat et son ExecutionPlan, sans seconde série de prompts. Fermeture du propriétaire ou token annulent la session avant lancement ; le cycle de vie natif relève du [contrat runtime](../../extensions/scripts/0057-pure-script-preparation.md).

Repères : ScriptPreparationDispatcher, QuickItemSelectorDialog, QuickStringSelectorDialog, FilePromptDialog, SimplePromptDialog et ScriptInputDialogControls. Restent ouverts : picker natif, autres hôtes/variantes, Wayland, DPI et accessibilité. Les anciennes suites du pont n’attestent pas le nouveau runner ; les [suites complètes 19bb](../../../../../artifacts/linux/scripts-runtime-20261018/full-02/parent-verification.json) lui sont propres. Paquet exact et runtime S01 ciblé vérifiés ; audit natif des saisies ouvert : [TODO](../../../todo.md#validation-courante--scripts-runtime).
