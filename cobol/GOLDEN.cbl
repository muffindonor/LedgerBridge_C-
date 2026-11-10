       IDENTIFICATION DIVISION.
       PROGRAM-ID. GOLDEN.
      *---------------------------------------------------------------
      * Writes one binary record per line of GOLDIN, storing each
      * value in a different COBOL numeric format. The C# tests decode
      * the binary file and compare the result with the plain text
      * input, so the expected values never pass through the decoder.
      *---------------------------------------------------------------
       ENVIRONMENT DIVISION.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT GOLD-IN  ASSIGN TO "GOLDIN"
               ORGANIZATION IS LINE SEQUENTIAL
               FILE STATUS IS FS-IN.
           SELECT GOLD-OUT ASSIGN TO "GOLDOUT"
               ORGANIZATION IS SEQUENTIAL
               FILE STATUS IS FS-OUT.

       DATA DIVISION.
       FILE SECTION.
       FD  GOLD-IN.
       01  IN-REC.
           05  IN-CASE                 PIC 9(04).
           05  IN-ODD                  PIC S9(11)V99
                                       SIGN IS LEADING SEPARATE.
           05  IN-EVEN                 PIC S9(10)V99
                                       SIGN IS LEADING SEPARATE.
           05  IN-UNS                  PIC 9(05).
           05  IN-MAX                  PIC S9(18)
                                       SIGN IS LEADING SEPARATE.
           05  IN-DISP                 PIC S9(05)V99
                                       SIGN IS LEADING SEPARATE.
           05  IN-B4                   PIC S9(04)
                                       SIGN IS LEADING SEPARATE.
           05  IN-B9                   PIC S9(09)
                                       SIGN IS LEADING SEPARATE.
           05  IN-B18                  PIC S9(18)
                                       SIGN IS LEADING SEPARATE.
       FD  GOLD-OUT.
           COPY GOLDREC.

       WORKING-STORAGE SECTION.
       01  FS-IN                       PIC X(02).
       01  FS-OUT                      PIC X(02).

       PROCEDURE DIVISION.
       0000-MAIN.
           OPEN INPUT GOLD-IN OUTPUT GOLD-OUT
           PERFORM UNTIL FS-IN = "10"
               READ GOLD-IN
                   NOT AT END
                       MOVE IN-CASE TO G-CASE
                       MOVE IN-ODD  TO G-PACK-ODD
                       MOVE IN-EVEN TO G-PACK-EVEN
                       MOVE IN-UNS  TO G-PACK-UNS
                       MOVE IN-MAX  TO G-PACK-MAX
                       MOVE IN-DISP TO G-DISP-OVER
                       MOVE IN-DISP TO G-DISP-LEAD
                       MOVE IN-DISP TO G-DISP-TRAIL
                       MOVE IN-B4   TO G-BIN-4
                       MOVE IN-B9   TO G-BIN-9
                       MOVE IN-B18  TO G-BIN-18
                       WRITE GOLD-REC
               END-READ
           END-PERFORM
           CLOSE GOLD-IN GOLD-OUT
           STOP RUN.
