# ADR 0021 · Encodage des messages

Statut : accepté.

Besoin : ne pas altérer les messages des dépôts configurés avec un encodage historique.

Décision : lire les templates et écrire brouillon/commit selon `i18n.commitEncoding`, UTF-8 par défaut. Valider strictement la représentation avant d'écrire ou de remplacer le brouillon ; un caractère incompatible reste éditable et produit une erreur. Lire les métadonnées Git en UTF-8 pour le bureau natif. Le codec des messages n'affecte pas les chemins ou les blobs.

Repères : `GitRepository.CommitEncoding`, `GitRepository.ReflogEncoding`, `GitRepository.Advanced`, `CommitDraftStore`, `GitLogQuery`, `GitRepository.BlameHistory`.
