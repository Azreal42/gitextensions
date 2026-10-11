# ADR 0013 · Avatars natifs

Statut : accepté ; raccord blame en cours.

Besoin : conserver les avatars et fallback initials de l'original, notamment dans Gource, avec annulation et cache local.

Décision : chaîne GitHub → Gravatar → initiales ; fournisseur PNG injectable, taille d'image demandée, cache XDG et mémoire bornée. Dix téléchargements concurrents comme l'original ; limites de taille/temps et annulation respectées. Aucun secret/API key à ajouter. Gravatar utilise le [hash SHA256 actuel](https://docs.gravatar.com/rest/hash/) tout en conservant les paramètres historiques.

Repères : `GourceAvatarProvider`, `GourceAvatarProtocol`, `GourcePlugin`, tests HTTP injectés.
