# 0131 — Fermer le sélecteur de fichiers avec le script

Statut : implémenté ; [validation et limites courantes](../../../todo.md#sélecteur-de-fichiers--périmètre-vérifié).

Annuler une préparation doit fermer son vrai sélecteur avant de rendre la main. `StorageProvider` ne permet pas cette fermeture : FilePrompt héberge le contrôle et le modèle publics d’Avalonia.Dialogs dans `ScriptFilePickerDialog`, possédé et attendu. Cette dépendance à l’API publique du namespace Internal reste confinée à cet adaptateur et à la version épinglée.

Le texte manuel reste littéral ; seul vide annule. Browse part de « . », remplace toute la saisie par les fichiers existants cités selon Windows, ou la conserve après Cancel. L’acceptation du picker tient compte du champ Nom du fichier : le modèle Avalonia d’ouverture ignore cette édition. Les chemins sélectionnés restent exacts ; un nom entier existant précède l’interprétation d’une liste citée. Aucune expansion shell.

Repères : `FilePromptDialog`, `ScriptFilePickerDialog`, `ScriptPreparationDispatcher`, `ScriptFilePickerTests`. Ce choix géré est local aux scripts ; aucun remplacement global des autres pickers.
