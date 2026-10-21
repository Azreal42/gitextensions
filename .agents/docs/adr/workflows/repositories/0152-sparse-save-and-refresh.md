# ADR 0152 · Enregistrer les règles sparse et rafraîchir le checkout

Statut : accepté, périmètre ciblé.

Besoin : préparer des règles sparse sans imposer immédiatement leur effet sur les fichiers, et retrouver la décision de sauvegarde Windows à la fermeture.

Save et Ctrl+Enter enregistrent les réglages puis ferment après succès. « Refresh working copy using the current settings and rules » est coché par défaut ; le décocher conserve l’index et les fichiers jusqu’au prochain rafraîchissement. Cette option seule ne rend pas l’édition sale. Fermer une édition modifiée propose Yes/No/Cancel : Yes sauve avec le choix de rafraîchissement, No abandonne le brouillon, Cancel reste dans l’éditeur. Un échec conserve le brouillon et révoque, comme Cancel, la demande de fermeture du propriétaire.

Les modes cone et sparse-index restent disponibles. Les règles enregistrées restent lisibles après désactivation ; un état désactivé ne signifie pas que le checkout a déjà été restauré. L’action Refresh séparée applique les réglages enregistrés, sans sauver les modifications en cours, et peut restaurer le checkout complet après une désactivation enregistrée sans Refresh.

Une fermeture pendant une sauvegarde déjà admise attend son drainage. La confirmation Linux de restauration immédiate reste distincte du Yes/No Windows qui ajuste le filtre avant read-tree ; elle ne remplace pas la question de fermeture sale.

Repères : `FormSparseWorkingCopy`, `FormSparseWorkingCopyViewModel`, `SparseCheckoutDialog` dans `RepositoryDialogs.cs`, `GitRepository.SparseCheckout.Save.cs`, `GitRepository.SparseCheckout.cs`, `DeferredOwnerCloseReplay`.
