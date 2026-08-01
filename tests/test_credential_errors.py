from labelsuite.core.ocr.textract_client import friendly_credential_error


class NoCredentialsError(Exception):
    pass


class EndpointConnectionError(Exception):
    pass


class ProfileNotFound(Exception):
    pass


class TestFriendlyCredentialError:
    def test_ec2_metadata_maps_to_not_configured(self):
        message = friendly_credential_error(Exception(
            "Unable to get IAM security credentials from EC2 Instance Metadata Service."))
        assert "자격증명" in message
        assert ".aws" in message

    def test_no_credentials_error_type(self):
        message = friendly_credential_error(NoCredentialsError("Unable to locate credentials"))
        assert "자격증명" in message

    def test_invalid_token_maps_to_bad_key(self):
        message = friendly_credential_error(Exception(
            "An error occurred (InvalidClientTokenId) when calling GetCallerIdentity"))
        assert "액세스 키가 잘못" in message

    def test_signature_mismatch_maps_to_bad_secret(self):
        message = friendly_credential_error(Exception("SignatureDoesNotMatch: ..."))
        assert "Secret Key" in message

    def test_timeout_maps_to_network(self):
        message = friendly_credential_error(EndpointConnectionError("endpoint"))
        assert "연결할 수 없습니다" in message

    def test_profile_not_found(self):
        message = friendly_credential_error(ProfileNotFound("myprofile"))
        assert "프로필" in message

    def test_unknown_error_passthrough(self):
        message = friendly_credential_error(Exception("weird failure"))
        assert "weird failure" in message
