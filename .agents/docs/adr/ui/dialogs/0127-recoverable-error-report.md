# ADR 0127 — Inspecter une erreur récupérable

L’action View details du statut ouvre une erreur récupérable sous son propre travail possédé, sans bloquer le catch par une modal automatique. Elle utilise une projection figée de l’exception : contexte déjà connu, hiérarchie, propriétés et détail long sélectionnable. ExceptionReportSnapshot isole les getters défaillants et les références cycliques ; ExceptionDetailsControl et ExceptionDetailDialog partagent cette lecture sans relancer Git pour collecter le contexte.

BugReportDialog garde les onglets et actions Windows adaptés à une erreur récupérable. Copier est explicite ; Send and Quit exige une description puis une prévisualisation/confirmation de la destination avant le launcher. Il ouvre une URL, sans soumission HTTP automatique. Les actions, enfants et fermetures sont possédés et drainés ; Quit reste une décision du parent.

Le circuit fatal NBug Windows, ses hooks de crash et toute garantie de récupération après corruption du processus restent distincts et non portés par cette fenêtre. Rapport/détails N2 sur le parcours récupérable ; [preuves et limites](../../../todo.md#portable-workflows--perimetre-verifie).
