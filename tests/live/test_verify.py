import unittest
from copy import deepcopy
from verify import VerificationFailure, check

SUBJECT = '11111111-1111-1111-1111-111111111111'
SOURCE = {'bot_id': 111, 'chat_id': 222, 'message_id': 333, 'reply_bot_id': 111,
          'reply_to': 333, 'reply_text': 'Значение: 7.8 mmol/L\nИсточник: 111/222/333'}
LAST = {'chat_id': 222, 'message_id': 334, 'reply_bot_id': 111, 'reply_to': 334,
        'reply_text': 'Значение: 7.8 mmol/L\nИсточник: 111/222/333'}
ROW = {'subject_id': SUBJECT, 'bot_id': 111, 'chat_id': 222, 'message_id': 333,
       'topic_id': None, 'value': 7.8, 'unit': 'mmol/L'}


class VerifierTests(unittest.TestCase):
    def test_exact_evidence_passes(self):
        check([ROW], SUBJECT, SOURCE, LAST)

    def test_missing_row_fails(self):
        with self.assertRaises(VerificationFailure):
            check([], SUBJECT, SOURCE, LAST)

    def test_wrong_value_fails(self):
        row = deepcopy(ROW)
        row['value'] = 9.8
        with self.assertRaises(VerificationFailure):
            check([row], SUBJECT, SOURCE, LAST)

    def test_wrong_source_fails(self):
        row = deepcopy(ROW)
        row['message_id'] = 332
        with self.assertRaises(VerificationFailure):
            check([row], SUBJECT, SOURCE, LAST)

    def test_duplicate_rows_and_unlinked_last_fail(self):
        with self.assertRaises(VerificationFailure):
            check([ROW, ROW], SUBJECT, SOURCE, LAST)
        last = dict(LAST, reply_to=999)
        with self.assertRaises(VerificationFailure):
            check([ROW], SUBJECT, SOURCE, last)


if __name__ == '__main__':
    unittest.main()
