# ADR 0102 · Complétion du message Commit

Socle `069de52b7` : full02, paquet isolé et natif source borné vérifiés. Style partagé corrigé sur `8d9c99259`, build/ciblés et natif Commit vérifiés séparément.

`CommitDialog` alimente `GitCommitAutoCompleteProvider` puis `CommitCompletionController` : noms/stems actuels et anciens, symboles des deux côtés MM. Extraction sur patch U5, puis HEAD courant/ancien, puis fichier de travail ; sous-module limité au basename. Les cinq préfixes restent `Co-authored-by: `, `Signed-off-by: `, `BREAKING CHANGE: `, `Reviewed-by: ` et `Tested-by: `.

Extracteur, règles et matching Windows sont liés. Après 200 ms et deux caractères, le popup suit le caret ; Ctrl+Espace accepte directement un résultat unique. Entrée/Tab/clic conservent sélection native, Undo et focus ; l’IME suspend l’interaction. Pendant le chargement, le premier Ctrl+Espace affiche une notice puis retourne comme Windows, sans rejeu.

Comme la ListBox Windows, `Controls/ClassicCompletionList` colore seulement la sélection, même quand une autre ligne est survolée. Les champs Clone/dépôt et Commit réutilisent ce style, appliqué aussi au présentateur réellement peint ; aucune reconstruction de contrôle ni modification du focus.

La complétion fonctionne sous Dictionary None. Ses exemptions NetSpell sont sensibles à la casse, uniquement en mémoire et append-only pendant la vie de l’éditeur, réinjectées au changement de moteur ; aucune écriture user.dic. Réponses périmées rejetées, travaux retirés drainés avant Closed.

Adaptations : statut du vrai Refresh réussi plutôt que requête/retry Windows, contexte Git capturé, configuration XDG `AutoCompleteRegexes.txt`, regex Compiled seule avec timeout visible de 250 ms, popup Avalonia. Code source et [preuves/limites](../../../todo.md#complétion-du-message-commit--périmètre-vérifié) restent les repères.
