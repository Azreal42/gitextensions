# ADR 0020 · Suggestions de clonage

Statut : accepté.

Besoin : retrouver les sources et destinations utilisées, avec les priorités de l'original et des chemins Linux littéraux.

Décision : source explicite, puis presse-papiers valide, historique et enfin remote de la branche courante (URL push avant fetch). Charger ces suggestions localement, sans interroger le serveur. Après acquittement du Process sans demande d'arrêt, un clone réussi rejoint les récents même si l'utilisateur choisit de garder son dépôt actuel ; erreur ou annulation ne modifie pas l'historique. Les captions peuvent échapper les contrôles, les valeurs transmises à Git restent exactes. Ne pas recopier les URL avec identifiants dans XDG.

Repères : `CloneDialog`, `CloneSourcesStore`, `RepositoryHistoryStore`, `GitRepository.CloneDefaults`, `MainWindow.Workflows`. Autocomplétion et aide restent ouvertes ; progression/acquittement : [ADR 0056](0056-owned-clone-process.md), audit natif dans le TODO.
