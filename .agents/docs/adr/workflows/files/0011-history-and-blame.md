# ADR 0011 · Historique fichier et blame

Statut : accepté.

Besoin : retrouver l'évolution d'un fichier renommé et l'auteur d'une ligne sans perdre le contexte de la révision affichée.

Décision : reprendre graphe/table en haut et onglets Commit/Diff/View/Blame en bas. Conserver le nom réel du fichier à chaque révision et décoder les échappements Git sans altérer les noms littéraux. Le graphe relie les ancêtres visibles ; l'ordre chronologique seul ne prouve pas l'ascendance. Blame affiche attribution et code avec défilement synchronisé. Images et enregistrement utilisent les octets de la révision.

Repères : `FormFileHistory`, `BlameControl`, `FileHistoryDialog`, `BlameView`, `GitRepository.BlameHistory`, tests History/Blame. Les parents simplifiés ne servent qu'à l'affichage ; Git résout les parents effectifs du diff.
