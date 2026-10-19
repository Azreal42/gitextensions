# ADR 0115 — Fenêtre Donate

Help ouvre la fenêtre de don avant toute ouverture externe. Conserver le texte traduit, le badge PNG et la destination historiques ; aucun appel réseau à l'affichage.

`DonateDialog` partage les fontes, palettes, activation et fermeture des fenêtres possédées. Seule l'activation du badge au pointeur ou au clavier appelle le launcher. Son erreur est présentée dans une modale possédée et laisse Donate ouverte. Le contrôle porte son indicateur de focus sans deuxième adorner Avalonia.

Le navigateur externe correspond au parcours Windows. Les tests de launcher ne constituent pas un don réel. Repères : `DonationsCatalog`, `MainWindow.ShowDonateAsync`, `DonateDialogTests` et `DonateMenuIntegrationTests`.
