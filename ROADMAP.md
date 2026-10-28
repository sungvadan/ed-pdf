# Folio — feuille de route

Éditeur PDF natif macOS, construit par étapes.

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
- ✅ Interface multilingue : français (source), anglais, allemand, espagnol, italien, portugais (Brésil), japonais,
  chinois simplifié — langue du système par défaut, ou choisie dans Réglages (au prochain lancement).
  Vérification : `scripts/l10n.py`
- ✅ Fichier > « Réduire la taille… » : copie réécrite par `PDFCompressor` (`Sources/FolioText/Compressor.swift`),
  sans réglage : images RVB/gris ramenées à 150 ppp (par rapport à la plus grande page) en JPEG qualité 0,6,
  objets inutilisés, `/PieceInfo`, `/Thumb` et métadonnées XMP retirés, flux identiques fusionnés, object streams
  + xref compressée. Texte et vectoriel intacts. Masques, CMJN, Indexed, JPEG 2000, JBIG2 laissés tels quels.
  Documents chiffrés : déchiffrés via PDFium (la copie n'est plus protégée). Test : `textlab compress in.pdf out.pdf`
- ✅ Ouverture des PDF protégés (écran de déverrouillage ; le mot de passe est conservé à l'enregistrement)
- ✅ Aplatir dans le document ouvert (pages sélectionnées ou tout), annulable — menu Pages / clic droit
- ✅ Exporter en images (PNG/JPEG, 72/150/300 ppp, toutes les pages ou la sélection)
- ✅ Caviardage : outil « Caviarder » (sur du texte → mots ; ailleurs → rectangle), puis « Appliquer le caviardage » :
  pages reconstruites en image 300 ppp + texte invisible pour les mots non caviardés (`Redaction.swift`)
- ✅ Signets (onglet de la barre latérale, ⌘B), icône de l'app (`scripts/make-icon.swift`), préférences (nom d'auteur)
- 🚧 Édition du texte existant, comme dans Word (voir `docs/text-editing.md`) :
  - ✅ Moteur : PDFium + chirurgie des content streams (texte non modifié identique au pixel), polices d'origine ou
    système équivalentes, styles en ligne conservés, reflux, alignements, vérification avec repli
  - ✅ Outil « Modifier le texte » : clic dans un paragraphe → édition sur place, ⌘Z
  - ⬜ Contrôles de style pendant l'édition, ajout de texte natif, déplacer un paragraphe, PDF protégés

# Version Windows (`windows/`, C# + Avalonia 11 + PDFium)

Construite sur le Mac : `scripts/build-windows.sh` → `build/windows/pdf-svd-arm64.exe` (Windows ARM, VM sur Mac
Apple Silicon) et `pdf-svd-x64.exe` (PC Intel/AMD), fichiers uniques sans installation. Test sur le Mac :
`cd windows && dotnet run -- fichier.pdf`. Mêmes traductions que la version Mac.

## ✅ Étape W1 — Lecture
- Ouvrir (bouton, Ctrl+O, glisser-déposer, fichier en argument), PDF protégés (mot de passe)
- Défilement continu, rendu PDFium net (selon la mise à l'échelle de l'écran), pages hors écran libérées
- Vignettes cliquables, n° de page modifiable, précédente/suivante, Début/Fin
- Zoom +/− (Ctrl +/−), taille réelle (Ctrl+0), ajuster à la largeur

## 🟨 Étape W2 — Recherche, sélection et copie du texte, impression, associer les .pdf à l'app
Fait (à tester dans la VM : impression et association, propres à Windows) :
- Menus Fichier / Édition ; champ de recherche (Ctrl+F, pendant la frappe, Entrée/F3 suivant, Maj+F3 précédent,
  compteur n/N, résultats surlignés, résultat courant en orange)
- Sélection du texte à la souris sur une page (double-clic = mot), Ctrl+A = page courante, Ctrl+C copie
- Imprimer… (Ctrl+P) : dialogue Windows, pages choisies, chaque page ajustée à la zone imprimable (GDI + `FPDF_RenderPage`)
- Inscription pour les .pdf (HKCU, à chaque lancement : « Ouvrir avec », Applications par défaut) ;
  Fichier > « Utiliser pdf-svd pour ouvrir les PDF… » ouvre Paramètres > Applications par défaut
Reste : sélection sur plusieurs pages
## ⬜ Étape W3 — Annotations (surlignage, notes, zones de texte, dessin, formes) et enregistrement
## ⬜ Étape W4 — Signatures, tampons, images
## ⬜ Étape W5 — Pages (pivoter, supprimer, réordonner, insérer, extraire), export, réduire la taille
## ⬜ Étape W6 — Caviardage, édition du texte existant

