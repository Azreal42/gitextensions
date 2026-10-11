# ADR 0020 · Suggestions de clonage

Statut : accepté.

Besoin : retrouver les sources et destinations utilisées, avec les priorités de l'original et des chemins Linux littéraux.

Décision : source explicite, puis presse-papiers valide, historique et enfin remote de la branche courante (URL push avant fetch). Charger ces suggestions localement, sans interroger le serveur. Un clone réussi rejoint les récents même si l'utilisateur choisit de garder son dépôt actuel ; erreur ou annulation ne modifie pas l'historique. Les captions peuvent échapper les contrôles, les valeurs transmises à Git restent exactes. Ne pas recopier les URL avec identifiants dans XDG.

Repères : `CloneDialog`, `CloneSourcesStore`, `RepositoryHistoryStore`, `GitRepository.CloneDefaults`, `MainWindow.Workflows`. Autocomplétion, aide et sortie continue restent ouvertes.
