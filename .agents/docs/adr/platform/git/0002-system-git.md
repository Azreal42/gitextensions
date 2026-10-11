# ADR 0002 · Git du système

Statut : accepté.

Besoin : conserver les hooks, configurations, transports et identifiants de l'utilisateur.

Décision : appeler Git sans shell, avec arguments séparés et parsing NUL des chemins. OpenSSH et les credential helpers restent sous le contrôle de Git ; aucune copie de secrets dans les préférences de l'application.

Conséquence : tests sur de vrais dépôts et noms difficiles ; annuler une commande doit terminer ses processus enfants et libérer l'UI.

Repères : `src/linux/GitExtensions.Core/`, `tests/linux/GitExtensions.Core.Tests/`.
