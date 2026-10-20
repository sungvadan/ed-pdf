# Folio — feuille de route

Éditeur PDF natif macOS (clone de PDF Expert), construit par étapes.

## ✅ Étape 1 — Lecteur
- Ouvrir / enregistrer / nouveau document (DocumentGroup), onglets et fichiers récents natifs
- Vignettes des pages dans la barre latérale
- Zoom (+, -, taille réelle, ajuster), modes page unique / continu / deux pages
- Navigation entre pages, indicateur « page x / n »
- Recherche plein texte avec surlignage et résultat suivant/précédent (⌘G / ⇧⌘G)

## ✅ Étape 2 — Annotations
- Barre d'outils : sélection, surligner, souligner, barrer, note, zone de texte, dessin, rectangle, ellipse, ligne, flèche
- Couleur par outil, épaisseur de trait
- Sélection (contour pointillé), déplacement, suppression (⌫), Échap → outil sélection
- Inspecteur : couleur, épaisseur, texte / commentaire
- Mise en forme des zones de texte : police, taille, gras, italique, souligné, barré, alignement, fond
  (`FreeTextAnnotation` dessine elle-même le texte ; rendu intégré au PDF enregistré, styles gardés dans `/FolioStyle`)
- Annuler / rétablir (UndoManager → marque le document comme modifié)
- Reste à faire plus tard : poignées de redimensionnement, remplissage des formes, édition du texte directement sur la page, liste des annotations

## ✅ Étape 3 — Gestion des pages
- Barre latérale SwiftUI : sélection multiple, glisser-déposer pour réordonner, menu contextuel
- Pivoter (⌘L / ⌘R), supprimer (⌥⌘⌫ ou ⌫ dans la barre latérale), dupliquer (⌘D)
- Insérer une page vierge (⌥⌘N), des PDF / images (⌥⌘I ou glisser des fichiers dans la barre latérale) → fusion
- Extraire les pages sélectionnées dans un nouveau PDF (⌥⌘E)
- Tout est annulable
- Plus tard : agrandir la taille des vignettes, glisser des pages vers le Finder / une autre fenêtre

## ✅ Étape 4 — Formulaires & signature
- Remplissage des champs (natif PDFKit) rendu annulable et détecté comme modification (`FormState`)
- Signatures dessinées (tracé vectoriel Ink) ou capturées sur papier (caméra, image, iPhone) avec extraction de l'encre sur fond transparent (`SignatureExtractor`), mémorisées (UserDefaults)
- Tampons : Approuvé, Rejeté, Confidentiel, Brouillon, Payé, date du jour + tampons personnalisés mémorisés (texte, couleur)
- Images (`ImageAnnotation`, PNG conservé dans `/FolioImage`)
- Redimensionnement : 4 poignées d'angle avec curseur dédié + largeur/hauteur dans l'inspecteur (proportions conservées pour signatures, dessins et images)
- Plus tard : signature tapée au clavier, création de champs de formulaire, signature numérique (certificat)

## ⬜ Étape 5 — Édition de texte & extras
- ✅ « Exporter pour envoi… » (⇧⌘E, bouton avion) : copie aplatie (texte conservé, liens web gardés),
  images réduites à 600 px, nom d'auteur retiré, données `/FolioImage` retirées — l'original n'est pas modifié
  (`SendExport.swift`). Limites : signets et liens internes perdus en mode aplati
- ✅ Export : mot de passe d'ouverture, réduction de taille (images JPEG optimisées écran)
- ✅ Ouverture des PDF protégés (écran de déverrouillage ; le mot de passe est conservé à l'enregistrement)
- ✅ Aplatir dans le document ouvert (pages sélectionnées ou tout), annulable — menu Pages / clic droit
- ✅ Exporter en images (PNG/JPEG, 72/150/300 ppp, toutes les pages ou la sélection)
- ✅ Caviardage : outil « Caviarder » (sur du texte → mots ; ailleurs → rectangle), puis « Appliquer le caviardage » :
  pages reconstruites en image 300 ppp + texte invisible pour les mots non caviardés (`Redaction.swift`)
- ✅ Signets (onglet de la barre latérale, ⌘B), icône de l'app (`scripts/make-icon.swift`), préférences (nom d'auteur)
- Édition du texte existant (difficile avec PDFKit : surcouche « effacer + réécrire »)
