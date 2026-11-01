from decimal import Decimal, InvalidOperation
from uuid import UUID


class VerificationFailure(Exception):
    pass


def check(rows, expected_subject, source, last):
    try:
        expected_subject = str(UUID(expected_subject))
        bot = int(source['bot_id'])
        chat = int(source['chat_id'])
        message = int(source['message_id'])
        last_message = int(last['message_id'])
        if not all((bot, chat, message, last_message)):
            raise ValueError('zero identifier')
        if len(rows) != 1:
            raise VerificationFailure('expected exactly one observation')
        row = rows[0]
        if (str(UUID(row['subject_id'])) != expected_subject or int(row['bot_id']) != bot
                or int(row['chat_id']) != chat or int(row['message_id']) != message
                or row['topic_id'] is not None or Decimal(str(row['value'])) != Decimal('7.8')
                or row['unit'] != 'mmol/L'):
            raise VerificationFailure('observation does not match independent expectation')
        text = f'Значение: 7.8 mmol/L\nИсточник: {bot}/{chat}/{message}'
        if (int(source['reply_bot_id']) != bot or int(source['reply_to']) != message
                or source['reply_text'] != text):
            raise VerificationFailure('record acknowledgement is not linked to the expected source')
        if (int(last['chat_id']) != chat or int(last['reply_bot_id']) != bot or int(last['reply_to']) != last_message
                or last['reply_text'] != text):
            raise VerificationFailure('last reply does not match the observation and command')
    except (KeyError, TypeError, ValueError, InvalidOperation) as error:
        raise VerificationFailure('malformed evidence') from error
