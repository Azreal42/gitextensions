# ADR 0022 · Retrouver les gestes de l’interface originale

Statut : accepté.

Besoin : conserver la disposition habituelle et les gestes clavier de Git Extensions, au-delà des couleurs et icônes.

Décision : Browse garde les positions dessous/gauche/droite du panneau Commit, le cycle/menu historique et sa préférence XDG ; dessous reste le défaut d’un nouveau profil. Déplacer le contrôle existant conserve sélection, contenu et diff. Ctrl+2 rend les informations accessibles dans toutes les positions.

Selection Filter de Commit sélectionne les fichiers non indexés par regex sans les masquer. Son historique reste de session ; Ctrl+F affiche/masque le champ compact. Stage all et la confirmation sans index concernent tous les fichiers affichés, indépendamment de cette sélection.

Reprendre les mnémoniques contextuelles des XLF, y compris leurs touches traduites, au moyen des contrôles d’accès natifs. Les libellés de texte ordinaires continuent d’être normalisés sans marqueur.

Repères : `MainWindow.Browser`, `MainWindow.Shortcuts`, `BrowserLayoutPreferencesStore`, `CommitDialog`, `TranslationCatalog`, `CloneDialog`, `CheckoutBranchDialog`.
