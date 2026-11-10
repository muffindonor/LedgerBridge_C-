      *---------------------------------------------------------------
      * ACCTPACK - packed account extract (52 bytes, fixed length)
      * Same fields as ACCTREC, with the balance in COMP-3.
      *---------------------------------------------------------------
       01  PACK-REC.
           05  PACK-ID                 PIC X(10).
           05  PACK-NAME               PIC X(25).
           05  PACK-TYPE               PIC X(01).
           05  PACK-STATUS             PIC X(01).
           05  PACK-BALANCE            PIC S9(11)V99 COMP-3.
           05  PACK-LAST-TXN-DATE      PIC 9(08).
