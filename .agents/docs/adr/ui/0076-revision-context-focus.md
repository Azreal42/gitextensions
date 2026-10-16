# ADR 0076 · Origine de navigation après clic droit

Statut : en validation ; Browse reste N2.

Besoin : fermer le menu d’un commit doit permettre immédiatement de naviguer depuis ce commit avec les flèches, y compris après F5 ou un clic provenant d’un autre champ.

Décision : donner le focus au conteneur sous le clic droit, même si le commit est déjà sélectionné, comme dans les menus de fichiers. Préserver la sélection multiple existante. Ne pas restaurer globalement le focus à la fermeture du menu : une action peut déjà avoir ouvert un dialogue de saisie.

Repères : MainWindow.RevisionMenu, RevisionContextFocusTests. Ancien comportement réellement rouge ; correctif isolé 32/32, onze nouveaux cas UI. Root 32/32 et paquet isolé vérifiés ; navigation native après Copy/Échap et F5 verte, mais première frappe Create branch rouge ; aucun nouveau résultat de suite complète attribué à cette source. Le maintien global du focus pendant F5 est hors de ce correctif.
