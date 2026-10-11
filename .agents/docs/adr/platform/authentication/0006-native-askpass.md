# ADR 0006 · Authentification interactive Linux

Statut : accepté.

Besoin : permettre les opérations distantes sans console visible, en conservant agents SSH et helpers Git configurés par l’utilisateur.

Décision : helper askpass natif éphémère comme fallback ; réponse uniquement sur stdout, secrets masqués, aucune persistance. Thème et langue suivent l’interface. Annulation interrompt les descendants du processus Git.

Repères : `GitRunner`, `AskpassApplication`, `AskpassWindow`, `gitextensions-askpass`, tests Linux.
