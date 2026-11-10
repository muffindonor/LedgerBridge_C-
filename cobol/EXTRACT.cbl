       IDENTIFICATION DIVISION.
       PROGRAM-ID. EXTRACT.
      *---------------------------------------------------------------
      * Converts the new account master into a fixed-length binary
      * extract with the balance in COMP-3, the shape a mainframe
      * extract usually takes. BANKBTCH only uses COMP-3 internally,
      * so without this LedgerBridge would never see real packed data.
      *
      * Input : ACCTIN  - account master, LINE SEQUENTIAL
      * Output: ACCTPCK - packed extract, fixed-length binary
      *---------------------------------------------------------------
       ENVIRONMENT DIVISION.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT ACCT-IN   ASSIGN TO "ACCTIN"
               ORGANIZATION IS LINE SEQUENTIAL
               FILE STATUS IS FS-IN.
           SELECT ACCT-PACK ASSIGN TO "ACCTPCK"
               ORGANIZATION IS SEQUENTIAL
               FILE STATUS IS FS-OUT.

       DATA DIVISION.
       FILE SECTION.
       FD  ACCT-IN.
           COPY ACCTREC.
       FD  ACCT-PACK.
           COPY ACCTPACK.

       WORKING-STORAGE SECTION.
       01  FS-IN                       PIC X(02).
       01  FS-OUT                      PIC X(02).

       PROCEDURE DIVISION.
       0000-MAIN.
           OPEN INPUT ACCT-IN OUTPUT ACCT-PACK
           IF FS-IN NOT = "00" OR FS-OUT NOT = "00"
               DISPLAY "EXTRACT: CANNOT OPEN FILES"
               MOVE 16 TO RETURN-CODE
               STOP RUN
           END-IF
           PERFORM UNTIL FS-IN = "10"
               READ ACCT-IN
                   NOT AT END
                       PERFORM 1000-WRITE-PACKED
               END-READ
           END-PERFORM
           CLOSE ACCT-IN ACCT-PACK
           STOP RUN.

       1000-WRITE-PACKED.
           MOVE ACCT-ID            TO PACK-ID
           MOVE ACCT-NAME          TO PACK-NAME
           MOVE ACCT-TYPE          TO PACK-TYPE
           MOVE ACCT-STATUS        TO PACK-STATUS
           MOVE ACCT-BALANCE       TO PACK-BALANCE
           MOVE ACCT-LAST-TXN-DATE TO PACK-LAST-TXN-DATE
           WRITE PACK-REC
           IF FS-OUT NOT = "00"
               DISPLAY "EXTRACT: WRITE ERROR"
               MOVE 16 TO RETURN-CODE
               STOP RUN
           END-IF.
