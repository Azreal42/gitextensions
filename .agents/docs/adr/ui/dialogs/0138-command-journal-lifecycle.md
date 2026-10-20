# ADR 0138 · Suivre les commandes dans le journal

Retrouver le diagnostic Windows pendant l’exécution d’une commande, en conservant le contrat Linux de métadonnées seules. Une ligne représente une opération depuis son lancement jusqu’à sa fin ; le PID n’est publié qu’après confirmation du lancement natif. Les outils externes restent exclus.

Conserver l’ordre de lancement et des identités stables, avec une borne de 500 entrées. Clear et l’éviction effacent le diagnostic sans arrêter Git ni réintroduire une commande à sa fin. Les notifications hors verrou et les erreurs des observateurs ne doivent jamais modifier l’opération Git.

F12 réutilise une fenêtre non modale globale. Liste et détail séparés, icônes et raccourcis Save/Copy/Clear, police monospace fixe et options locales wrap/topmost reprennent Windows. Une sélection ancienne reste consultable ; la dernière ligne suit les nouvelles commandes seulement si elle était déjà sélectionnée. Le recyclage répare uniquement un focus de liste réellement perdu, dans cette fenêtre active et disponible ; aucun rappel de fenêtre ni vol de focus au détail, menu ou picker.

Le répertoire est visible, mais arguments, environnement, entrées/sorties et erreurs brutes restent exclus du diagnostic. Copy annonce honnêtement la copie du seul descripteur. L’export TXT/CSV est explicite, sur un snapshot antérieur au picker ; préserver Unicode et séparateurs culturels. Une réponse tardive après fermeture ne démarre pas d’écriture. Le picker sans token et une écriture déjà ouverte ne forment pas une transaction.

Le vrai cache de résultats et son onglet, les piles, l’indicateur UI-thread, les autres processus et la taille/position restent des compléments. Les données brutes de Windows restent un écart de confidentialité volontaire. La recherche n’est pas une fonction de cette fenêtre Windows. La restauration de groupe par Openbox puis F12 ne prouve pas une restauration par F12 seul ; la validation inter-Browse correspondante est distincte.

Repères : Windows CommandLog/CommandCache/FormGitCommandLog ; Linux GitCommandLog/GitRunner, GitCommandLogWindow/GitCommandLogPresentation et MainWindow.ToolsHelp. Preuves et limites dans le [TODO](../../../todo.md#journal-de-commandes).
