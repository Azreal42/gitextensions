# ADR 0076 · Origine de navigation après clic droit

Statut : clic droit et fermeture de menu testés sur 189c18a6c ; Browse reste N2.

Besoin : fermer le menu d’un commit doit permettre immédiatement de naviguer depuis ce commit avec les flèches, y compris après F5 ou un clic provenant d’un autre champ.

Décision : donner le focus au conteneur sous le clic droit, même si le commit est déjà sélectionné, comme dans les menus de fichiers. Préserver la sélection multiple existante. Ne pas restaurer globalement le focus à la fermeture du menu : une action peut déjà avoir ouvert un dialogue de saisie.

Repères : MainWindow.RevisionMenu, RevisionContextFocusTests. Ancien comportement réellement rouge ; correctif isolé 32/32, onze nouveaux cas inclus dans les 1190 tests Desktop complets sur 189. Navigation native après Copy/Échap, origine autre champ et ligne déjà sélectionnée après F5 vérifiées. La première frappe du dialogue est traitée par ADR 0077 ; retour au navigateur après Cancel et maintien global pendant F5 restent hors de ce correctif.
