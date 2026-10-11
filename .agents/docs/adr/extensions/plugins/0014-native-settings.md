# ADR 0014 · Dialogues Proxy et Gource

Statut : accepté.

Besoin : retrouver les choix et dispositions des plugins originaux ; utiliser les exécutables Linux configurés sans imposer de téléchargement Windows.

Décision : dialogues natifs dédiés. Proxy montre les portées Git locale/globale et garde le mot de passe hors préférences XDG. Gource choisit un exécutable et conserve les avatars temporaires jusqu'à la fin du viewer ; annulation et erreurs ne laissent pas un lancement partiel.

Repères : `ProxySwitcherDialog`, `GourceDialog`, `MainWindow.Plugins`, tests de lifecycle et processus local contrôlé. Le rendu OpenGL de Gource réel reste à valider.
