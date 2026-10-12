# Release signing certificates

Public certificates (`*.cer`) with which releases are signed. They are shipped with the server; its self-update only
installs a setup that carries a valid signature of one of them. Create a certificate with
`scripts/new-signing-cert.sh`; the private key never belongs in the repository.

To change the certificate, add the new one here and keep the old one for at least one release: installed servers
verify the next update against the certificates of *their* version.
