# ADR 0098 · Appearance et préférences partagées

Statut : intégré sur `192069d34`, ciblé 63/63. Appearance reste N1 dans la matrice de référence ; full frais, paquet et natif restent à accepter.

Retrouver les groupes et clés XLF Windows avec des réglages ayant un effet réel. Dates relatives, branche dashboard/récents, visibilité de l'avatar du graphe et langue sont raccordées ; le thème reste un ajout Linux. Les neuf autres préférences Windows restent désactivées ou adaptées, avec explication. L'avatar du graphe reste une initiale locale. Help translate utilise le launcher existant ; les quatre autres actions restent indisponibles.

`AppearanceSettingsSession` fusionne les seules intentions modifiées dans les autorités Desktop et View existantes. General écrit Desktop puis acquitte ces intentions avant les profils suivants : un retry après échec partiel préserve les publications concurrentes. Les booléens View inchangés restent temporaires ; Sorting conserve sa publication explicite historique. Apply garde la fenêtre ouverte, Cancel abandonne seulement le brouillon restant. Langue inconnue conservée jusqu'au choix explicite ; redémarrage requis.

Dashboard et récents partagent les métadonnées par dépôt et contexte Git capturé. Le premier affichage réutilise la prélecture de Main ; F5 et les revisites invalident les métadonnées. Une décoration d'un autre dépôt ne bloque pas l'historique actif. Settings peut cesser d'attendre sans tuer un worker utilisé par Browse ; Main annule et draine ses workers avant Closed. Même test de fermeture rouge puis vert après ajout du token consommateur.

La branche est affichée par défaut comme Windows ; le caption raccourci et le chemin cible restent indépendants. Linux l'ajoute en suffixe, alors que Windows réserve une colonne : rapprochement visuel encore ouvert.

Portée et résultats dans [TODO](../../../todo.md#appearance--socle-intégré-validation-en-cours) et [failure](../../../failure.md#appearance--brouillons-et-consommateurs). Aucun ciblé ne certifie la parité globale.
