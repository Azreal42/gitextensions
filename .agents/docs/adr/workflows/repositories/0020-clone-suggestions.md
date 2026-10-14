# ADR 0020 · Suggestions de clonage

Statut : accepté.

Besoin : retrouver les sources et destinations utilisées, avec les priorités de l'original et des chemins Linux littéraux.

Décision : source explicite, puis presse-papiers valide, historique et enfin remote de la branche courante (URL push avant fetch). Charger ces suggestions localement, sans interroger le serveur. Après acquittement du Process sans demande d'arrêt, un clone réussi rejoint les récents même si l'utilisateur choisit de garder son dépôt actuel ; erreur ou annulation ne modifie pas l'historique. Les captions peuvent échapper les contrôles, les valeurs transmises à Git restent exactes. Ne pas recopier les URL avec identifiants dans XDG.

La destination propose les dossiers locaux avec ajout du suffixe sélectionné ; Entrée/Tab accepte, Échap restaure la saisie. Les captions montrent le nom lisible et le chemin complet en infobulle, sans modifier la valeur. Charger les branches à la demande en gardant le formulaire éditable ; une nouvelle source/requête invalide la réponse précédente et le choix saisi reste prioritaire. Annuler et attendre cette lecture avant Clone ou fermeture. F1 et « ? » ouvrent la section Clone du manuel de la même version majeure/mineure.

Un chemin contenant CR/LF reste éditable et copiable littéralement dans un champ replié à hauteur bornée ; une valeur ordinaire rétablit le champ compact historique. Échapper uniquement les captions et l’aperçu, jamais la valeur du contrôle.

Repères : `CloneDialog`, `CloneDirectoryCompletion`, `CloneSourcesStore`, `RepositoryHistoryStore`, `GitRepository.CloneDefaults`, `MainWindow.Workflows`, `CloneCompletionTests`, `CloneBranchLoadingTests`. Progression/acquittement : [ADR 0056](0056-owned-clone-process.md), validations et limites dans le TODO.
