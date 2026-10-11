# ADR 0017 · Préférences et contexte du blâme

Statut : accepté.

Besoin : retrouver les options d'attribution et d'affichage, les informations auteur/committer et les actions sur la ligne choisie.

Décision : réglages XDG partagés entre Blame et l'historique fichier ; reprendre les défauts et traductions originales. Les informations de commit se chargent à la demande, sans bloquer la lecture du fichier ; fermeture/rechargement annulent les lectures. Le clic droit vise sa propre ligne, indépendamment du caret.

Repères : `BlamePreferencesStore`, `BlameDialog`, `BlameView`, raccords `FileHistoryDialog`.
