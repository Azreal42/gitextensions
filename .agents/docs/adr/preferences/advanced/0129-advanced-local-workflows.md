# ADR 0129 — Régler les parcours locaux existants

AdvancedSettingsPage reprend six champs utiles de la page Windows : toujours afficher Checkout, réutiliser son action locale, forcer avec lease Commit & Push lors d’Amend, normaliser les noms et leur symbole, et afficher les options avancées. Les trois premiers ajoutent une commande de réglage à des consommateurs déjà présents ; AlwaysAdvanced reste limité à Remotes. Le symbole propose `_`, `-` et `(none)` — valeur vide ; un littéral hérité inconnu reste visible sans réécriture implicite.

AdvancedSettingsSession écrit uniquement les intentions modifiées dans les trois profils existants. Les conflits observés arrêtent la sauvegarde ; les reçus par fichier permettent de conserver les publications acquises et de réessayer le reste. Cancel abandonne le brouillon suivant, sans transaction globale. Le Save typé de PreferencesStore conserve seulement les clés étrangères de premier niveau valides ; il ne devient pas un writer CAS pour tous ses champs.

Updates/RC, console et DontShowHelpImages restent hors de cette page partielle ; SSH est distinct. N2 partiel, [preuves et périmètre](../../../todo.md#portable-workflows--perimetre-verifie).
