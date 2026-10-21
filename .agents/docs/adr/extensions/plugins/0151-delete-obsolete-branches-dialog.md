# ADR 0151 · Suppression des branches obsolètes

Statut : accepté. Dialogue et neuf réglages vérifiés en Git/headless ; preuves et limites dans le TODO.

Une fenêtre dédiée reprend les cinq colonnes, cases, présélection par âge et deux décisions Windows pour les remotes. L’âge suggère une sélection et n’interdit pas de cocher une branche récente. Le moteur typé commun garde l’adaptateur plugin historique et ses résultats ; aucune cellule affichée ne sert à fabriquer une commande Git.

Toute la sélection et ses tips sont relus avant la première mutation ; les leases distantes et protections Git des worktrees restent actives. Les résultats par branche distinguent succès, échec, interruption et éléments non tentés. Une tentative peut avoir un effet sans succès connu : le reçu May est distinct du reçu acquis. Aucun succès ni atomicité de lot n’est déduit d’un processus interrompu.

L’activation regex appartient au moteur partagé : un flag explicite false neutralise le texte conservé ; son absence garde la compatibilité des anciens profils. Recherches et mutations sont annulées/drainées par le dialogue.

Les neuf réglages Windows du plugin ont une page dédiée dans Settings > Plugins. Elle partage le XML Local/Distributed/Global et ses intentions par clé ; Effective reste en lecture seule. La lecture cherche le namespace GUID Windows, puis son ancien préfixe nominal, puis les valeurs JSON historiques. Aucune ouverture ne migre les profils. Les clés inconnues et les préférences voisines restent intactes ; chaque fichier sauvé est acquitté avant le suivant, sans transaction entre fichiers ni relecture externe silencieuse. Cancel abandonne uniquement les brouillons non sauvés.

Le preview reprend ces valeurs à son ouverture ; Search change seulement les critères de l’invocation, sans créer une seconde autorité JSON. Son bouton Settings ferme et draine le preview avant d’ouvrir la vraie page, avec le propriétaire et le token du script conservés. Une fermeture du propriétaire scelle la continuation avant le retour du modal et reprend la fermeture différée après drainage.

Le spinner General garde sa forme Int32 Windows ; les nouvelles éditions de l’âge sont validées à 0..100000, borne du formulaire métier et du moteur existants. Une valeur brute héritée non éditée n’est pas réécrite. Ce complément ne constitue pas un éditeur générique ni un catalogue d’activation pour tous les plugins Windows ; sa qualification reste distincte de celle du dialogue.
